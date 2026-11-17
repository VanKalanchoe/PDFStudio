using PDFiumCore;
using PdfStudio.Core.Pdfium;
using SkiaSharp;

namespace PdfStudio.Core.Documents;

/// <summary>
/// Renders PDF pages into DPI-aware <see cref="SKBitmap"/> buffers using PDFium.
/// </summary>
public sealed class PdfPageRenderer
{
    private const int FpdfBitmapBgra = 4;

    public SKBitmap RenderPage(
        PdfDocumentSession session,
        int pageIndex,
        int pixelWidth,
        int pixelHeight,
        PdfRenderOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (pageIndex < 0 || pageIndex >= session.Pages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        pixelWidth = Math.Clamp(pixelWidth, 1, 8192);
        pixelHeight = Math.Clamp(pixelHeight, 1, 8192);
        options ??= new PdfRenderOptions();

        cancellationToken.ThrowIfCancellationRequested();

        var bitmap = new SKBitmap(new SKImageInfo(pixelWidth, pixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
        try
        {
            PdfiumEngine.Instance.RunLocked(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var doc = session.Handle;
                var page = fpdfview.FPDF_LoadPage(doc, pageIndex);
                if (page is null || page.__Instance == IntPtr.Zero)
                {
                    throw new InvalidOperationException($"Failed to load PDF page {pageIndex + 1}.");
                }

                try
                {
                    IntPtr pixels = bitmap.GetPixels();
                    int stride = bitmap.RowBytes;

                    var fpdfBitmap = fpdfview.FPDFBitmapCreateEx(
                        pixelWidth,
                        pixelHeight,
                        FpdfBitmapBgra,
                        pixels,
                        stride);

                    if (fpdfBitmap is null || fpdfBitmap.__Instance == IntPtr.Zero)
                    {
                        throw new InvalidOperationException("Failed to create PDFium bitmap wrapper.");
                    }

                    try
                    {
                        fpdfview.FPDFBitmapFillRect(fpdfBitmap, 0, 0, pixelWidth, pixelHeight, options.BackgroundArgb);

                        int flags = 0;
                        if (options.RenderAnnotations)
                        {
                            flags |= (int)RenderFlags.RenderAnnotations;
                        }

                        if (options.OptimizeTextForLcd)
                        {
                            flags |= (int)RenderFlags.OptimizeTextForLcd;
                        }

                        fpdfview.FPDF_RenderPageBitmap(
                            fpdfBitmap,
                            page,
                            0,
                            0,
                            pixelWidth,
                            pixelHeight,
                            0,
                            flags);
                    }
                    finally
                    {
                        fpdfview.FPDFBitmapDestroy(fpdfBitmap);
                    }
                }
                finally
                {
                    fpdfview.FPDF_ClosePage(page);
                }
            });

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    public Task<SKBitmap> RenderPageAsync(
        PdfDocumentSession session,
        int pageIndex,
        int pixelWidth,
        int pixelHeight,
        PdfRenderOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () => RenderPage(session, pageIndex, pixelWidth, pixelHeight, options, cancellationToken),
            cancellationToken);
    }

    public Task<SKBitmap> RenderThumbnailAsync(
        PdfDocumentSession session,
        int pageIndex,
        int maxWidthPx = 140,
        double dpiScale = 1.0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (pageIndex < 0 || pageIndex >= session.Pages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        var page = session.Pages[pageIndex];
        double aspect = page.HeightPoints > 0 ? page.HeightPoints / page.WidthPoints : 1.414;
        int width = Math.Max(16, (int)Math.Round(maxWidthPx * dpiScale));
        int height = Math.Max(16, (int)Math.Round(width * aspect));

        var opts = new PdfRenderOptions(
            RenderAnnotations: true,
            OptimizeTextForLcd: false,
            BackgroundArgb: 0xFFFFFFFF);

        return RenderPageAsync(session, pageIndex, width, height, opts, cancellationToken);
    }
}
