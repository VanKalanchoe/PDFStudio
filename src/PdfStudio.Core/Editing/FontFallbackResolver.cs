using System.Runtime.InteropServices;
using System.Text;
using HarfBuzzSharp;
using SkiaSharp;

namespace PdfStudio.Core.Editing;

public sealed record ResolvedFontAsset(
    string FamilyName,
    byte[] FontBytes,
    int UnitsPerEm,
    bool IsFallback);

public sealed record ShapedTextSegment(
    string Text,
    ResolvedFontAsset Font,
    double WidthPoints);

public sealed record WrappedTextLine(
    IReadOnlyList<ShapedTextSegment> Segments,
    double TotalWidthPoints);

public sealed record FontCoverageResolution(
    ResolvedFontAsset PrimaryFont,
    bool UsedFallbackFont,
    int MissingGlyphCount,
    string SummaryFamilyName);

/// <summary>
/// Inspects Unicode glyph coverage with HarfBuzzSharp + SkiaSharp, synthesizes/embeds
/// matching system fallback TrueType fonts for missing glyphs, and wraps lines accurately.
/// </summary>
public sealed class FontFallbackResolver
{
    public FontCoverageResolution ResolveCoverage(
        string preferredFamily,
        bool isBold,
        bool isItalic,
        string text)
    {
        var style = CreateFontStyle(isBold, isItalic);
        string mappedFamily = MapPdfFontFamily(preferredFamily);

        using var primaryTypeface = SKFontManager.Default.MatchFamily(mappedFamily, style)
                                 ?? SKFontManager.Default.MatchFamily("Arial", style)
                                 ?? SKTypeface.Default;

        var primaryAsset = LoadFontAsset(primaryTypeface, isFallback: false);
        var missingCodepoints = FindMissingCodepoints(primaryAsset.FontBytes, text);

        if (missingCodepoints.Count == 0)
        {
            return new FontCoverageResolution(
                PrimaryFont: primaryAsset,
                UsedFallbackFont: false,
                MissingGlyphCount: 0,
                SummaryFamilyName: primaryAsset.FamilyName);
        }

        // Attempt to find a unified fallback typeface that covers all missing codepoints + primary text
        using var unifiedFallback = FindBestUnifiedFallback(mappedFamily, style, text, missingCodepoints);
        if (unifiedFallback is not null)
        {
            var fallbackAsset = LoadFontAsset(unifiedFallback, isFallback: true);
            return new FontCoverageResolution(
                PrimaryFont: fallbackAsset,
                UsedFallbackFont: true,
                MissingGlyphCount: missingCodepoints.Count,
                SummaryFamilyName: fallbackAsset.FamilyName);
        }

        return new FontCoverageResolution(
            PrimaryFont: primaryAsset,
            UsedFallbackFont: true,
            MissingGlyphCount: missingCodepoints.Count,
            SummaryFamilyName: primaryAsset.FamilyName);
    }

    public List<WrappedTextLine> WrapAndShapeText(
        string text,
        string preferredFamily,
        double fontSize,
        bool isBold,
        bool isItalic,
        double maxWidthPoints,
        bool autoReflow,
        double letterSpacingEm,
        out FontCoverageResolution coverage)
    {
        coverage = ResolveCoverage(preferredFamily, isBold, isItalic, text);
        var style = CreateFontStyle(isBold, isItalic);

        string normalized = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        var rawParagraphs = normalized.Split('\n');
        var result = new List<WrappedTextLine>();

        double effectiveMaxWidth = autoReflow && maxWidthPoints > 12.0
            ? (maxWidthPoints * 1.03) + 4.0
            : double.PositiveInfinity;

        // Per-codepoint fallback cache for mixed-script lines
        var fontCache = new Dictionary<string, ResolvedFontAsset>(StringComparer.OrdinalIgnoreCase)
        {
            [coverage.PrimaryFont.FamilyName] = coverage.PrimaryFont
        };

        foreach (string rawLine in rawParagraphs)
        {
            if (rawLine.Length == 0)
            {
                result.Add(new WrappedTextLine([], 0));
                continue;
            }

            if (double.IsInfinity(effectiveMaxWidth))
            {
                result.Add(ShapeLineWithFallbackRuns(
                    rawLine,
                    coverage.PrimaryFont,
                    style,
                    fontSize,
                    letterSpacingEm,
                    fontCache));
                continue;
            }

            var wrappedStrings = WrapSingleParagraph(
                rawLine,
                coverage.PrimaryFont,
                fontSize,
                letterSpacingEm,
                effectiveMaxWidth);

            foreach (var lineStr in wrappedStrings)
            {
                result.Add(ShapeLineWithFallbackRuns(
                    lineStr,
                    coverage.PrimaryFont,
                    style,
                    fontSize,
                    letterSpacingEm,
                    fontCache));
            }
        }

        return result;
    }

    private static List<string> WrapSingleParagraph(
        string paragraph,
        ResolvedFontAsset font,
        double fontSize,
        double letterSpacingEm,
        double maxWidthPoints)
    {
        var lines = new List<string>();
        var words = TokenizeWords(paragraph);
        if (words.Count == 0)
        {
            lines.Add(string.Empty);
            return lines;
        }

        var current = new StringBuilder();
        foreach (string word in words)
        {
            string candidate = current.Length == 0 ? word : $"{current} {word}";
            double candidateWidth = MeasureWidthHarfBuzz(font, candidate, fontSize, letterSpacingEm);

            if (current.Length == 0 || candidateWidth <= maxWidthPoints)
            {
                current.Clear();
                current.Append(candidate);
            }
            else
            {
                lines.Add(current.ToString());
                current.Clear();
                current.Append(word);
            }
        }

        if (current.Length > 0)
        {
            lines.Add(current.ToString());
        }

        return lines;
    }

    private static List<string> TokenizeWords(string input)
    {
        var tokens = new List<string>();
        int i = 0;
        while (i < input.Length)
        {
            while (i < input.Length && char.IsWhiteSpace(input[i]))
            {
                i++;
            }

            if (i >= input.Length)
            {
                break;
            }

            int start = i;
            while (i < input.Length && !char.IsWhiteSpace(input[i]))
            {
                i++;
            }

            tokens.Add(input[start..i]);
        }

        return tokens;
    }

    private static WrappedTextLine ShapeLineWithFallbackRuns(
        string lineText,
        ResolvedFontAsset primaryFont,
        SKFontStyle style,
        double fontSize,
        double letterSpacingEm,
        Dictionary<string, ResolvedFontAsset> fontCache)
    {
        if (string.IsNullOrEmpty(lineText))
        {
            return new WrappedTextLine([], 0);
        }

        var segments = new List<ShapedTextSegment>();
        var currentRun = new StringBuilder();
        ResolvedFontAsset? currentFont = null;

        foreach (Rune rune in lineText.EnumerateRunes())
        {
            ResolvedFontAsset targetFont = primaryFont;
            if (!Rune.IsWhiteSpace(rune) && !HasGlyph(primaryFont.FontBytes, rune.Value))
            {
                using var matched = SKFontManager.Default.MatchCharacter(primaryFont.FamilyName, style, null, rune.Value);
                if (matched is not null)
                {
                    if (!fontCache.TryGetValue(matched.FamilyName, out var cached))
                    {
                        cached = LoadFontAsset(matched, isFallback: true);
                        fontCache[matched.FamilyName] = cached;
                    }

                    targetFont = cached;
                }
            }

            if (currentFont is null)
            {
                currentFont = targetFont;
            }
            else if (!string.Equals(currentFont.FamilyName, targetFont.FamilyName, StringComparison.OrdinalIgnoreCase))
            {
                string segText = currentRun.ToString();
                double w = MeasureWidthHarfBuzz(currentFont, segText, fontSize, letterSpacingEm);
                segments.Add(new ShapedTextSegment(segText, currentFont, w));
                currentRun.Clear();
                currentFont = targetFont;
            }

            currentRun.Append(rune.ToString());
        }

        if (currentRun.Length > 0 && currentFont is not null)
        {
            string segText = currentRun.ToString();
            double w = MeasureWidthHarfBuzz(currentFont, segText, fontSize, letterSpacingEm);
            segments.Add(new ShapedTextSegment(segText, currentFont, w));
        }

        double totalWidth = segments.Sum(s => s.WidthPoints);
        return new WrappedTextLine(segments, totalWidth);
    }

    public static double MeasureWidthHarfBuzz(
        ResolvedFontAsset fontAsset,
        string text,
        double fontSize,
        double letterSpacingEm = 0.0)
    {
        if (string.IsNullOrEmpty(text) || fontAsset.FontBytes.Length == 0)
        {
            return 0;
        }

        using var hb = CreateHarfBuzzFont(fontAsset.FontBytes, out int unitsPerEm);
        using var buffer = new HarfBuzzSharp.Buffer();
        buffer.AddUtf16(text);
        buffer.GuessSegmentProperties();
        hb.Shape(buffer);

        var positions = buffer.GlyphPositions;
        long totalAdvance = 0;
        for (int i = 0; i < positions.Length; i++)
        {
            totalAdvance += positions[i].XAdvance;
        }

        double shapedWidth = totalAdvance * (fontSize / Math.Max(1, unitsPerEm));
        double extraTracking = Math.Max(0, text.Length - 1) * (letterSpacingEm * fontSize);
        return Math.Max(0, shapedWidth + extraTracking);
    }

    private static List<int> FindMissingCodepoints(byte[] fontBytes, string text)
    {
        var missing = new List<int>();
        if (string.IsNullOrEmpty(text) || fontBytes.Length == 0)
        {
            return missing;
        }

        using var hb = CreateHarfBuzzFont(fontBytes, out _);
        var seen = new HashSet<int>();
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune) || !seen.Add(rune.Value))
            {
                continue;
            }

            if (!hb.TryGetNominalGlyph(rune.Value, out uint glyph) || glyph == 0)
            {
                missing.Add(rune.Value);
            }
        }

        return missing;
    }

    private static bool HasGlyph(byte[] fontBytes, int codepoint)
    {
        if (fontBytes.Length == 0)
        {
            return false;
        }

        using var hb = CreateHarfBuzzFont(fontBytes, out _);
        return hb.TryGetNominalGlyph(codepoint, out uint glyph) && glyph != 0;
    }

    private static SKTypeface? FindBestUnifiedFallback(
        string preferredFamily,
        SKFontStyle style,
        string fullText,
        List<int> missingCodepoints)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Segoe UI",
            "Arial",
            "Noto Sans",
            "DejaVu Sans"
        };

        foreach (int cp in missingCodepoints)
        {
            using var matched = SKFontManager.Default.MatchCharacter(preferredFamily, style, null, cp);
            if (matched is not null && !string.IsNullOrWhiteSpace(matched.FamilyName))
            {
                candidates.Add(matched.FamilyName);
            }
        }

        foreach (string candidateName in candidates)
        {
            var tf = SKFontManager.Default.MatchFamily(candidateName, style);
            if (tf is null)
            {
                continue;
            }

            var bytes = ReadTypefaceBytes(tf);
            if (bytes.Length > 0 && FindMissingCodepoints(bytes, fullText).Count == 0)
            {
                return tf;
            }

            tf.Dispose();
        }

        // Fall back to first character match if no single font covers everything
        return missingCodepoints.Count > 0
            ? SKFontManager.Default.MatchCharacter(preferredFamily, style, null, missingCodepoints[0])
            : null;
    }

    private static ResolvedFontAsset LoadFontAsset(SKTypeface typeface, bool isFallback)
    {
        byte[] bytes = ReadTypefaceBytes(typeface);
        int upem = 1000;
        if (bytes.Length > 0)
        {
            using var hb = CreateHarfBuzzFont(bytes, out upem);
        }

        return new ResolvedFontAsset(
            FamilyName: string.IsNullOrWhiteSpace(typeface.FamilyName) ? "Arial" : typeface.FamilyName,
            FontBytes: bytes,
            UnitsPerEm: upem,
            IsFallback: isFallback);
    }

    private static byte[] ReadTypefaceBytes(SKTypeface typeface)
    {
        using var stream = typeface.OpenStream();
        if (stream is null || !stream.HasLength || stream.Length <= 0)
        {
            return [];
        }

        var bytes = new byte[stream.Length];
        int read = stream.Read(bytes, bytes.Length);
        return read == bytes.Length ? bytes : bytes[..read];
    }

    private static HarfBuzzSharp.Font CreateHarfBuzzFont(byte[] fontBytes, out int unitsPerEm)
    {
        var handle = GCHandle.Alloc(fontBytes, GCHandleType.Pinned);
        try
        {
            var blob = new Blob(handle.AddrOfPinnedObject(), fontBytes.Length, MemoryMode.ReadOnly, () =>
            {
                if (handle.IsAllocated)
                {
                    handle.Free();
                }
            });

            var face = new Face(blob, 0);
            unitsPerEm = Math.Max(1, face.UnitsPerEm);
            var font = new HarfBuzzSharp.Font(face);
            font.SetScale(unitsPerEm, unitsPerEm);
            return font;
        }
        catch
        {
            if (handle.IsAllocated)
            {
                handle.Free();
            }

            throw;
        }
    }

    private static SKFontStyle CreateFontStyle(bool isBold, bool isItalic)
    {
        var weight = isBold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal;
        var slant = isItalic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright;
        return new SKFontStyle(weight, SKFontStyleWidth.Normal, slant);
    }

    public static string MapPdfFontFamily(string family)
    {
        if (string.IsNullOrWhiteSpace(family))
        {
            return "Arial";
        }

        string trimmed = family.Trim();
        return trimmed.ToLowerInvariant() switch
        {
            "helvetica" or "arialmt" or "arial-boldmt" or "arial-italicmt" => "Arial",
            "times" or "times-roman" or "timesnewromanpsmt" or "timesnewromanps" => "Times New Roman",
            "courier" or "couriernewpsmt" or "couriernewps" => "Courier New",
            _ => trimmed
        };
    }
}
