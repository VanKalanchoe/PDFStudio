using PdfStudio.Core.Documents;
using SkiaSharp;

namespace PdfStudio.Core.Tests;

public sealed class PdfPageRendererTests
{
    [Fact]
    public async Task RenderPageAsync_RendersDpiAwareNonBlankBitmap()
    {
        var bytes = TestPdfGenerator.CreateSampleTwoPagePdf();
        var service = new PdfDocumentService();
        var renderer = new PdfPageRenderer();

        using var session = await service.OpenAsync(bytes, "render_test.pdf");
        using var bitmap = await renderer.RenderPageAsync(session, pageIndex: 0, pixelWidth: 612, pixelHeight: 792);

        Assert.Equal(612, bitmap.Width);
        Assert.Equal(792, bitmap.Height);
        Assert.Equal(SKColorType.Bgra8888, bitmap.ColorType);

        // Verify the blue rectangle drawn on page 1 is present (non-white pixels exist)
        bool hasNonWhitePixel = false;
        for (int y = 0; y < bitmap.Height && !hasNonWhitePixel; y += 4)
        {
            for (int x = 0; x < bitmap.Width; x += 4)
            {
                var color = bitmap.GetPixel(x, y);
                if (color.Red < 245 || color.Green < 245 || color.Blue < 245)
                {
                    hasNonWhitePixel = true;
                    break;
                }
            }
        }

        Assert.True(hasNonWhitePixel, "Expected rendered PDF page to contain non-white content pixels.");
    }

    [Fact]
    public async Task RenderThumbnailAsync_RespectsAspectRatioAndDpiScale()
    {
        var bytes = TestPdfGenerator.CreateSampleTwoPagePdf();
        var service = new PdfDocumentService();
        var renderer = new PdfPageRenderer();

        using var session = await service.OpenAsync(bytes, "thumb_test.pdf");
        using var thumb = await renderer.RenderThumbnailAsync(session, pageIndex: 0, maxWidthPx: 140, dpiScale: 2.0);

        Assert.Equal(280, thumb.Width);
        int expectedHeight = (int)Math.Round(280 * (792.0 / 612.0));
        Assert.Equal(expectedHeight, thumb.Height);
    }

    [Fact]
    public async Task RenderPageAsync_ConcurrentCalls_RemainThreadSafe()
    {
        var bytes = TestPdfGenerator.CreateSampleTwoPagePdf();
        var service = new PdfDocumentService();
        var renderer = new PdfPageRenderer();

        using var session = await service.OpenAsync(bytes, "concurrent.pdf");

        var tasks = Enumerable.Range(0, 8).Select(i =>
            renderer.RenderThumbnailAsync(session, i % 2, maxWidthPx: 120));

        var bitmaps = await Task.WhenAll(tasks);
        try
        {
            Assert.Equal(8, bitmaps.Length);
            Assert.All(bitmaps, bmp => Assert.True(bmp.Width == 120 && bmp.Height > 100));
        }
        finally
        {
            foreach (var bmp in bitmaps)
            {
                bmp.Dispose();
            }
        }
    }
}
