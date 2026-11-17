using PDFiumCore;

namespace PdfStudio.Core.Pdfium;

/// <summary>
/// Manages thread-safe initialization, synchronization, and teardown of the native PDFium library.
/// PDFium native APIs are not thread-safe across calls; all P/Invoke calls must execute inside <see cref="RunLocked{T}"/>.
/// </summary>
public sealed class PdfiumEngine
{
    private static readonly Lazy<PdfiumEngine> LazyInstance = new(() => new PdfiumEngine());

    private readonly object _gate = new();
    private bool _initialized;
    private bool _destroyed;

    private PdfiumEngine()
    {
    }

    public static PdfiumEngine Instance => LazyInstance.Value;

    public void EnsureInitialized()
    {
        lock (_gate)
        {
            if (_destroyed)
            {
                throw new ObjectDisposedException(nameof(PdfiumEngine), "PDFium library has already been shut down.");
            }

            if (!_initialized)
            {
                fpdfview.FPDF_InitLibrary();
                _initialized = true;
            }
        }
    }

    public T RunLocked<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            EnsureInitialized();
            return action();
        }
    }

    public void RunLocked(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            EnsureInitialized();
            action();
        }
    }

    public void Shutdown()
    {
        lock (_gate)
        {
            if (_initialized && !_destroyed)
            {
                fpdfview.FPDF_DestroyLibrary();
                _destroyed = true;
                _initialized = false;
            }
        }
    }

    public static string DescribeLastError(uint errorCode) => errorCode switch
    {
        0 => "No error.",
        1 => "Unknown PDFium error.",
        2 => "File not found or could not be opened.",
        3 => "File is not in valid PDF format or is corrupted.",
        4 => "Password required or incorrect password.",
        5 => "Unsupported security scheme.",
        6 => "Page not found or content error.",
        _ => $"PDFium error code {errorCode}."
    };
}
