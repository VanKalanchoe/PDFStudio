using PdfStudio.Core.Documents;
using PdfStudio.Core.Editing;
using PdfStudio.Core.Layout;
using PdfStudio.Core.Ocr;
using SkiaSharp;

namespace PdfStudio.Core.Tests;

public sealed class WorkflowToolsTests
{
    [Fact]
    public async Task AnnotationService_HighlightAndComment_PersistAndRender()
    {
        const string stream = "BT /F1 12 Tf 72 700 Td (Highlight this text) Tj ET\n";
        var bytes = TestPdfGenerator.CreateSinglePagePdfFromStream(stream);
        var docService = new PdfDocumentService();
        var annotService = new PdfAnnotationService();
        var renderer = new PdfPageRenderer();

        using var session = await docService.OpenAsync(bytes, "annot.pdf");
        var hl = annotService.AddHighlight(session, 0, new PdfRect(70, 78, 200, 96));
        var note = annotService.AddComment(session, 0, 220, 80, "Review Q4 numbers", "Reviewer");

        Assert.Equal(PdfAnnotationKind.Highlight, hl.Kind);
        Assert.Equal(PdfAnnotationKind.Comment, note.Kind);

        byte[] saved = session.SaveToBytes();
        using var reopened = await docService.OpenAsync(saved, "annot_reopened.pdf");
        var loaded = annotService.GetAnnotations(reopened, 0);

        Assert.Equal(2, loaded.Count);
        Assert.Contains(loaded, a => a.Kind == PdfAnnotationKind.Highlight);
        Assert.Contains(loaded, a => a.Kind == PdfAnnotationKind.Comment && a.Contents == "Review Q4 numbers");

        using var bmp = await renderer.RenderPageAsync(reopened, 0, 612, 792);
        var pixel = bmp.GetPixel(190, 86); // inside yellow highlight area
        Assert.True(pixel.Red > 200 && pixel.Green > 180 && pixel.Blue < 180,
            $"Expected yellow highlight tint, got ({pixel.Red},{pixel.Green},{pixel.Blue})");
    }

    [Fact]
    public async Task ImageEditor_InsertImage_RendersAtTargetBoundsAndPersists()
    {
        var bytes = TestPdfGenerator.CreateSampleTwoPagePdf();
        var docService = new PdfDocumentService();
        var imageEditor = new PdfImageEditor();
        var renderer = new PdfPageRenderer();

        byte[] pngBytes = CreateSolidPng(64, 64, new SKColor(220, 40, 40, 255));

        using var session = await docService.OpenAsync(bytes, "image.pdf");
        bool inserted = imageEditor.InsertImage(session, 0, pngBytes, new PdfRect(320, 400, 420, 500));
        Assert.True(inserted);

        byte[] saved = session.SaveToBytes();
        using var reopened = await docService.OpenAsync(saved, "image_reopened.pdf");
        using var bmp = await renderer.RenderPageAsync(reopened, 0, 612, 792);

        var px = bmp.GetPixel(370, 450);
        Assert.True(px.Red > 190 && px.Green < 80 && px.Blue < 80,
            $"Expected inserted red image pixel, got ({px.Red},{px.Green},{px.Blue})");
    }

    [Fact]
    public async Task ImageEditor_MoveResizeAndDeleteImage_UpdatesPageAndPersists()
    {
        var bytes = TestPdfGenerator.CreateSampleTwoPagePdf();
        var docService = new PdfDocumentService();
        var imageEditor = new PdfImageEditor();
        var renderer = new PdfPageRenderer();

        byte[] pngBytes = CreateSolidPng(64, 64, new SKColor(30, 180, 60, 255));

        using var session = await docService.OpenAsync(bytes, "move_image.pdf");
        Assert.True(imageEditor.InsertImage(session, 0, pngBytes, new PdfRect(100, 220, 200, 320)));

        var images = imageEditor.GetImages(session, 0);
        var inserted = Assert.Single(images);

        var movedBounds = new PdfRect(300, 420, 460, 560);
        Assert.True(imageEditor.SetImageBounds(session, 0, inserted.ObjectIndex, movedBounds));

        byte[] movedBytes = session.SaveToBytes();
        using var movedSession = await docService.OpenAsync(movedBytes, "moved.pdf");
        var movedImg = Assert.Single(imageEditor.GetImages(movedSession, 0));
        Assert.InRange(movedImg.Bounds.Left, 298, 302);
        Assert.InRange(movedImg.Bounds.Top, 418, 422);
        Assert.InRange(movedImg.Bounds.Width, 158, 162);
        Assert.InRange(movedImg.Bounds.Height, 138, 142);

        using (var bmp = await renderer.RenderPageAsync(movedSession, 0, 612, 792))
        {
            var newPx = bmp.GetPixel(380, 490);
            Assert.True(newPx.Green > 150 && newPx.Red < 80,
                $"Expected green image at moved location, got ({newPx.Red},{newPx.Green},{newPx.Blue})");

            var oldPx = bmp.GetPixel(150, 270);
            Assert.True(oldPx.Red > 240 && oldPx.Green > 240 && oldPx.Blue > 240,
                $"Expected white paper at previous location, got ({oldPx.Red},{oldPx.Green},{oldPx.Blue})");
        }

        Assert.True(imageEditor.DeleteImage(movedSession, 0, movedImg.ObjectIndex));
        Assert.Empty(imageEditor.GetImages(movedSession, 0));
    }

    [Fact]
    public async Task RedactionService_StripsCoveredTextAndRendersBlackBar()
    {
        const string stream =
            "BT /F1 12 Tf 72 700 Td (Public Heading) Tj ET\n" +
            "BT /F1 12 Tf 72 650 Td (CONFIDENTIAL SECRET) Tj ET\n";

        var bytes = TestPdfGenerator.CreateSinglePagePdfFromStream(stream);
        var docService = new PdfDocumentService();
        var detector = new TextBlockDetector();
        var redactor = new PdfRedactionService();
        var renderer = new PdfPageRenderer();

        using var session = await docService.OpenAsync(bytes, "redact.pdf");
        var layoutBefore = detector.DetectBlocks(session, 0);
        var secretBlock = layoutBefore.Blocks.Single(b => b.Text.Contains("CONFIDENTIAL"));

        var result = redactor.ApplyRedaction(session, 0, secretBlock.Bounds);
        Assert.True(result.Success);
        Assert.True(result.StrippedObjectCount >= 1);

        byte[] saved = session.SaveToBytes();
        using var reopened = await docService.OpenAsync(saved, "redact_reopened.pdf");
        var layoutAfter = detector.DetectBlocks(reopened, 0);
        string textAfter = string.Join("\n", layoutAfter.Blocks.Select(b => b.Text));

        Assert.Contains("Public Heading", textAfter);
        Assert.DoesNotContain("CONFIDENTIAL", textAfter);
        Assert.DoesNotContain("SECRET", textAfter);

        using var bmp = await renderer.RenderPageAsync(reopened, 0, 612, 792);
        var px = bmp.GetPixel((int)secretBlock.Bounds.CenterX, (int)secretBlock.Bounds.CenterY);
        Assert.True(px.Red < 15 && px.Green < 15 && px.Blue < 15,
            $"Expected black redaction bar, got ({px.Red},{px.Green},{px.Blue})");
    }

    [Fact]
    public async Task PageOrganizer_InsertMergeReorderAndDelete_WorksCleanly()
    {
        var single = TestPdfGenerator.CreateSinglePagePdfFromStream("BT /F1 12 Tf 72 700 Td (Single Origin) Tj ET\n");
        var twoPage = TestPdfGenerator.CreateSampleTwoPagePdf();
        var docService = new PdfDocumentService();
        var organizer = new PdfPageOrganizer();
        var detector = new TextBlockDetector();

        using var session = await docService.OpenAsync(single, "org.pdf");
        Assert.Single(session.Pages);

        organizer.InsertBlankPage(session, 1);
        Assert.Equal(2, session.Pages.Count);

        int merged = organizer.MergePdf(session, twoPage, 2);
        Assert.Equal(2, merged);
        Assert.Equal(4, session.Pages.Count);

        organizer.DeletePage(session, 1); // remove blank page -> [Single Origin, Milestone 1, Page Two]
        Assert.Equal(3, session.Pages.Count);

        using var reordered = organizer.ReorderPage(session, 2, 0); // [Page Two, Single Origin, Milestone 1]
        Assert.Equal(3, reordered.Pages.Count);

        var firstPageText = string.Join(" ", detector.DetectBlocks(reordered, 0).Blocks.Select(b => b.Text));
        Assert.Contains("Page Two Summary", firstPageText);
    }

    [Fact]
    public async Task OcrService_ScannedImagePdf_RecognizesTextAndEmbedsSearchableLayer()
    {
        var emptyPdf = TestPdfGenerator.CreateSinglePagePdfFromStream("");
        var docService = new PdfDocumentService();
        var imageEditor = new PdfImageEditor();
        var detector = new TextBlockDetector();
        var ocr = new PdfOcrService();

        byte[] scannedPng = CreateScannedTextPng("INVOICE 2026 TOTAL");
        using var session = await docService.OpenAsync(emptyPdf, "scanned.pdf");
        Assert.True(imageEditor.InsertImage(session, 0, scannedPng, new PdfRect(50, 80, 520, 220)));

        // Before OCR: no text objects exist
        Assert.Empty(detector.DetectBlocks(session, 0).Blocks);

        var ocrResult = await ocr.RunOcrOnPageAsync(session, 0, "eng", embedInvisibleTextLayer: true);

        Assert.True(ocrResult.EmbeddedSearchableText);
        Assert.NotEmpty(ocrResult.Lines);
        Assert.Contains("INVOICE", ocrResult.FullText, StringComparison.OrdinalIgnoreCase);

        // After OCR: TextBlockDetector detects the embedded searchable text layer
        var layoutAfter = detector.DetectBlocks(session, 0);
        Assert.NotEmpty(layoutAfter.Blocks);
        string detectedText = string.Join(" ", layoutAfter.Blocks.Select(b => b.Text));
        Assert.Contains("INVOICE", detectedText, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] CreateSolidPng(int width, int height, SKColor color)
    {
        using var bmp = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(color);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static byte[] CreateScannedTextPng(string text)
    {
        using var bmp = new SKBitmap(1200, 360);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.White);

        using var typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold) ?? SKTypeface.Default;
        using var paint = new SKPaint
        {
            Typeface = typeface,
            TextSize = 84,
            Color = SKColors.Black,
            IsAntialias = true
        };
        canvas.DrawText(text, 60, 200, paint);

        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
