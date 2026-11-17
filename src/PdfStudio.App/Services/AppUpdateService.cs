using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

namespace PdfStudio.App.Services;

public sealed record AppUpdateCheckResult(
    bool IsUpdateAvailable,
    string CurrentVersion,
    string LatestVersion,
    string TagName,
    string? DownloadUrl,
    string ReleasePageUrl,
    long AssetSizeBytes);

/// <summary>
/// Checks GitHub Releases for newer PdfStudio builds and applies portable self-updates.
/// </summary>
public sealed class AppUpdateService
{
    private const string ReleasesApiUrl = "https://api.github.com/repos/VanKalanchoe/PDFStudio/releases?per_page=10";
    private const string ReleasesPageUrl = "https://github.com/VanKalanchoe/PDFStudio/releases";

    private static readonly HttpClient Http = CreateHttpClient();

    public static string CurrentVersionString
    {
        get
        {
            var v = typeof(AppUpdateService).Assembly.GetName().Version;
            return v is null ? "0.0.1" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"PdfStudio/{CurrentVersionString}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public async Task<AppUpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await Http.GetAsync(ReleasesApiUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        var currentVer = ParseNormalizedVersion(CurrentVersionString);
        AppUpdateCheckResult? bestMatch = null;
        Version bestVer = currentVer;

        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draftProp) && draftProp.GetBoolean())
            {
                continue;
            }

            string tag = release.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() ?? string.Empty : string.Empty;
            var candidateVer = ParseNormalizedVersion(tag);
            if (candidateVer <= bestVer)
            {
                continue;
            }

            string htmlUrl = release.TryGetProperty("html_url", out var htmlProp)
                ? htmlProp.GetString() ?? ReleasesPageUrl
                : ReleasesPageUrl;

            (string? downloadUrl, long size) = FindPlatformAsset(release);
            bestVer = candidateVer;
            bestMatch = new AppUpdateCheckResult(
                IsUpdateAvailable: true,
                CurrentVersion: CurrentVersionString,
                LatestVersion: $"{candidateVer.Major}.{candidateVer.Minor}.{candidateVer.Build}",
                TagName: tag,
                DownloadUrl: downloadUrl,
                ReleasePageUrl: htmlUrl,
                AssetSizeBytes: size);
        }

        return bestMatch ?? new AppUpdateCheckResult(
            IsUpdateAvailable: false,
            CurrentVersion: CurrentVersionString,
            LatestVersion: CurrentVersionString,
            TagName: $"v{CurrentVersionString}",
            DownloadUrl: null,
            ReleasePageUrl: ReleasesPageUrl,
            AssetSizeBytes: 0);
    }

    public async Task<bool> DownloadAndApplyUpdateAsync(
        AppUpdateCheckResult update,
        IProgress<string>? statusProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(update.DownloadUrl) || !CanUpdateInPlace())
        {
            OpenUrlInBrowser(update.ReleasePageUrl);
            return false;
        }

        string tempRoot = Path.Combine(Path.GetTempPath(), "PdfStudioUpdate", Guid.NewGuid().ToString("N"));
        string zipPath = Path.Combine(tempRoot, "update.zip");
        string extractDir = Path.Combine(tempRoot, "extracted");
        Directory.CreateDirectory(tempRoot);

        using (var response = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? update.AssetSizeBytes;
            await using var remoteStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var localStream = File.Create(zipPath);

            byte[] buffer = new byte[81920];
            long received = 0;
            int read;
            int lastPercent = -1;
            while ((read = await remoteStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await localStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                received += read;
                if (total > 0)
                {
                    int pct = (int)Math.Clamp((received * 100L) / total, 0, 100);
                    if (pct != lastPercent && (pct % 5 == 0 || pct == 100))
                    {
                        lastPercent = pct;
                        statusProgress?.Report($"{pct}%");
                    }
                }
            }
        }

        ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

        if (OperatingSystem.IsWindows())
        {
            return LaunchWindowsUpdaterScript(tempRoot, extractDir);
        }

        if (OperatingSystem.IsMacOS())
        {
            return LaunchMacOsUpdaterScript(tempRoot, extractDir);
        }

        OpenUrlInBrowser(update.ReleasePageUrl);
        return false;
    }

    public static bool CanUpdateInPlace()
    {
        string baseDir = AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(baseDir) || !Directory.Exists(baseDir))
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            string exePath = Path.Combine(baseDir, "PdfStudio.App.exe");
            if (!File.Exists(exePath))
            {
                return false;
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            if (!baseDir.Contains(".app/Contents/MacOS", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        try
        {
            string probe = Path.Combine(baseDir, $".pdfstudio_write_probe_{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool LaunchWindowsUpdaterScript(string tempRoot, string extractDir)
    {
        string appDir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        int pid = Environment.ProcessId;
        string scriptPath = Path.Combine(tempRoot, "apply_update.ps1");

        string script = $$"""
            $ErrorActionPreference = 'Stop'
            $targetPid = {{pid}}
            $src = '{{EscapePsSingleQuote(extractDir)}}'
            $dst = '{{EscapePsSingleQuote(appDir)}}'
            $tempRoot = '{{EscapePsSingleQuote(tempRoot)}}'

            for ($i = 0; $i -lt 40; $i++) {
                if (-not (Get-Process -Id $targetPid -ErrorAction SilentlyContinue)) { break }
                Start-Sleep -Milliseconds 250
            }

            Copy-Item -Path "$src\*" -Destination $dst -Recurse -Force
            Start-Process -FilePath (Join-Path $dst 'PdfStudio.App.exe')
            Remove-Item -Path $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
            """;

        File.WriteAllText(scriptPath, script);

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false
        });
        return true;
    }

    private static bool LaunchMacOsUpdaterScript(string tempRoot, string extractDir)
    {
        string macOsDir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        string? contentsDir = Directory.GetParent(macOsDir)?.FullName;
        string? targetAppDir = contentsDir is not null ? Directory.GetParent(contentsDir)?.FullName : null;
        string? sourceAppDir = Directory.GetDirectories(extractDir, "*.app").FirstOrDefault();
        if (string.IsNullOrEmpty(targetAppDir) || string.IsNullOrEmpty(sourceAppDir))
        {
            return false;
        }

        int pid = Environment.ProcessId;
        string scriptPath = Path.Combine(tempRoot, "apply_update.sh");
        string script = $"""
            #!/bin/bash
            while kill -0 {pid} 2>/dev/null; do sleep 0.25; done
            ditto "{sourceAppDir}" "{targetAppDir}"
            xattr -cr "{targetAppDir}" 2>/dev/null || true
            open "{targetAppDir}"
            rm -rf "{tempRoot}"
            """;

        File.WriteAllText(scriptPath, script);
        Process.Start("chmod", $"+x \"{scriptPath}\"")?.WaitForExit(3000);
        Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = $"\"{scriptPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false
        });
        return true;
    }

    private static string EscapePsSingleQuote(string value) => value.Replace("'", "''");

    private static void OpenUrlInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Non-fatal if OS browser launch fails
        }
    }

    private static (string? DownloadUrl, long Size) FindPlatformAsset(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return (null, 0);
        }

        string expectedSuffix = OperatingSystem.IsMacOS() ? "macos-x64.zip" : "win-x64.zip";
        foreach (var asset in assets.EnumerateArray())
        {
            string name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
            if (!name.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            long size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out long bytes) ? bytes : 0;
            return (url, size);
        }

        return (null, 0);
    }

    internal static Version ParseNormalizedVersion(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new Version(0, 0, 0);
        }

        string cleaned = raw.Trim().TrimStart('v', 'V');
        int dashIdx = cleaned.IndexOf('-');
        if (dashIdx >= 0)
        {
            cleaned = cleaned[..dashIdx];
        }

        if (Version.TryParse(cleaned, out var parsed))
        {
            return new Version(
                Math.Max(0, parsed.Major),
                Math.Max(0, parsed.Minor),
                Math.Max(0, parsed.Build));
        }

        return new Version(0, 0, 0);
    }
}
