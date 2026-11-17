using PdfStudio.Core.Documents;
using PdfStudio.Core.Editing;
using PdfStudio.Core.Layout;

namespace PdfStudio.Core.Tests;

public sealed class PdfTextEditorTests
{
    [Fact]
    public void FontFallbackResolver_WrapsTextToTargetWidth()
    {
        var resolver = new FontFallbackResolver();
        const string text = "Enterprise revenue accelerated across cloud licensing and subscription renewals.";

        var lines = resolver.WrapAndShapeText(
            text: text,
            preferredFamily: "Arial",
            fontSize: 11.0,
            isBold: false,
            isItalic: false,
            maxWidthPoints: 180.0,
            autoReflow: true,
            letterSpacingEm: 0.0,
            out var coverage);

        Assert.True(lines.Count >= 3, $"Expected wrapped lines >= 3, got {lines.Count}");
        Assert.All(lines, l => Assert.True(l.TotalWidthPoints <= 195.0));
        Assert.False(string.IsNullOrWhiteSpace(coverage.SummaryFamilyName));
    }

    [Fact]
    public async Task ApplyTextEdit_CommitsUnicodeTextAndRoundTripsThroughSave()
    {
        const string stream =
            "BT /F2 20 Tf 72 700 Td (Old Report Title) Tj ET\n" +
            "BT /F1 11 Tf 72 650 Td (Original paragraph line one.) Tj ET\n" +
            "BT /F1 11 Tf 72 634 Td (Original paragraph line two.) Tj ET\n";

        var bytes = TestPdfGenerator.CreateSinglePagePdfFromStream(stream);
        var docService = new PdfDocumentService();
        var detector = new TextBlockDetector();
        var editor = new PdfTextEditor();
        var renderer = new PdfPageRenderer();

        using var session = await docService.OpenAsync(bytes, "edit_roundtrip.pdf");
        var layout = detector.DetectBlocks(session, 0);
        var paragraph = layout.Blocks[1];

        var editResult = editor.ApplyTextEdit(session, new PdfTextEditRequest(
            OriginalBlock: paragraph,
            NewText: "Updated Umsatzbericht: 18,4% Wachstum (EUR € / äöüß).",
            FontFamily: "Arial",
            FontSize: 11.0,
            IsBold: false,
            IsItalic: false,
            FillColor: new PdfColor(20, 40, 90, 255),
            Alignment: PdfTextAlignment.Left,
            LineSpacingRatio: 1.4,
            LetterSpacingEm: 0.0,
            TargetBounds: paragraph.Bounds with { Right = paragraph.Bounds.Left + 260 },
            AutoReflow: true));

        Assert.True(editResult.Success, editResult.DiagnosticMessage);
        Assert.True(editResult.RemovedObjectCount >= 1);
        Assert.True(editResult.InsertedObjectCount >= 1);

        byte[] savedBytes = session.SaveToBytes();
        Assert.True(savedBytes.Length > 200);

        // Re-open the saved PDF and verify both rendering and extracted text!
        using var reopened = await docService.OpenAsync(savedBytes, "reopened.pdf");
        var reopenedLayout = detector.DetectBlocks(reopened, 0);
        string allText = string.Join("\n", reopenedLayout.Blocks.Select(b => b.Text));

        Assert.Contains("Old Report Title", allText);
        Assert.DoesNotContain("Original paragraph line one", allText);
        Assert.Contains("Updated Umsatzbericht", allText);
        Assert.Contains("äöüß", allText);

        using var bmp = await renderer.RenderPageAsync(reopened, 0, 612, 792);
        Assert.Equal(612, bmp.Width);
        Assert.Equal(792, bmp.Height);
    }

    [Fact]
    public async Task SaveToFile_OverwritesOpenedFileCleanly()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"pdfstudio_save_{Guid.NewGuid():N}.pdf");
        try
        {
            await File.WriteAllBytesAsync(tempPath, TestPdfGenerator.CreateSampleTwoPagePdf());
            var docService = new PdfDocumentService();
            var detector = new TextBlockDetector();
            var editor = new PdfTextEditor();

            using (var session = await docService.OpenAsync(tempPath))
            {
                var layout = detector.DetectBlocks(session, 0);
                var firstBlock = layout.Blocks[0];
                var result = editor.ApplyTextEdit(session, new PdfTextEditRequest(
                    OriginalBlock: firstBlock,
                    NewText: "Saved Back In-Place",
                    FontFamily: "Arial",
                    FontSize: 18,
                    IsBold: true,
                    IsItalic: false,
                    FillColor: new PdfColor(0, 0, 0, 255),
                    Alignment: PdfTextAlignment.Left,
                    LineSpacingRatio: 1.35,
                    LetterSpacingEm: 0.0,
                    TargetBounds: firstBlock.Bounds with { Right = firstBlock.Bounds.Left + 300 },
                    AutoReflow: true));

                Assert.True(result.Success);
                session.SaveToFile(tempPath);
            }

            using var reopened = await docService.OpenAsync(tempPath);
            var reopenedLayout = detector.DetectBlocks(reopened, 0);
            Assert.Contains("Saved Back In-Place", string.Join("\n", reopenedLayout.Blocks.Select(b => b.Text)));
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    [Fact]
    public async Task ApplyTextEdit_UnbalancedCtmAndVectorLines_PreservesLinesAndUntouchedFontSizes()
    {
        const string stream =
            "0.5 0 0 0.5 0 0 cm\n" +
            "0 0 0 RG 2 w 100 1400 m 1100 1400 l S\n" +
            "BT /F1 24 Tf 144 1300 Td (Date: 17.11.2026) Tj ET\n" +
            "BT /F1 24 Tf 144 1240 Td (Keep untouched) Tj ET\n";

        var bytes = TestPdfGenerator.CreateSinglePagePdfFromStream(stream);
        var docService = new PdfDocumentService();
        var detector = new TextBlockDetector();
        var editor = new PdfTextEditor();
        var renderer = new PdfPageRenderer();

        using var session = await docService.OpenAsync(bytes, "unbalanced_ctm.pdf");
        var layoutBefore = detector.DetectBlocks(session, 0);
        Assert.Equal(2, layoutBefore.Blocks.Count);

        var dateBlock = layoutBefore.Blocks[0];
        var untouchedBefore = layoutBefore.Blocks[1];
        Assert.Equal(12.0, dateBlock.FontSize, precision: 1);
        Assert.Equal(12.0, untouchedBefore.FontSize, precision: 1);

        var editResult = editor.ApplyTextEdit(session, new PdfTextEditRequest(
            OriginalBlock: dateBlock,
            NewText: "Date: 18.11.2026",
            FontFamily: dateBlock.PrimaryFontFamily,
            FontSize: dateBlock.FontSize,
            IsBold: dateBlock.IsBold,
            IsItalic: dateBlock.IsItalic,
            FillColor: dateBlock.PrimaryColor,
            Alignment: dateBlock.Alignment,
            LineSpacingRatio: dateBlock.LineSpacingRatio,
            LetterSpacingEm: dateBlock.LetterSpacingEm,
            TargetBounds: dateBlock.Bounds,
            AutoReflow: false));

        Assert.True(editResult.Success);
        byte[] saved = session.SaveToBytes();

        using var reopened = await docService.OpenAsync(saved, "unbalanced_ctm_after.pdf");
        var layoutAfter = detector.DetectBlocks(reopened, 0);
        Assert.Equal(2, layoutAfter.Blocks.Count);

        var editedAfter = layoutAfter.Blocks[0];
        var untouchedAfter = layoutAfter.Blocks[1];

        Assert.Equal("Date: 18.11.2026", editedAfter.Text);
        Assert.Equal(12.0, editedAfter.FontSize, precision: 1);
        Assert.Equal("Keep untouched", untouchedAfter.Text);
        Assert.Equal(12.0, untouchedAfter.FontSize, precision: 1);
        Assert.Equal(untouchedBefore.Bounds.Top, untouchedAfter.Bounds.Top, precision: 1);
        Assert.Equal(untouchedBefore.Bounds.Left, untouchedAfter.Bounds.Left, precision: 1);

        // Verify the horizontal vector line at Y=700 (top-down Y=92) is still rendered dark on the page
        using var bmp = await renderer.RenderPageAsync(reopened, 0, 612, 792);
        bool hasVectorLinePixel = false;
        for (int y = 89; y <= 95; y++)
        {
            var pixel = bmp.GetPixel(300, y);
            if (pixel.Red < 180 && pixel.Green < 180 && pixel.Blue < 180)
            {
                hasVectorLinePixel = true;
                break;
            }
        }

        Assert.True(hasVectorLinePixel, "Expected horizontal vector line to remain intact after edit.");
    }

    [Fact]
    public async Task ApplyTextEdit_ShadedTableCellsAndSequentialEdits_PreservesGrayShadingAndBorders()
    {
        const string stream =
            "0.75 g 50 700 100 28 re f 150 700 220 28 re f\n" +
            "0 0 0 RG 1 w 50 728 m 370 728 l S 50 700 m 370 700 l S 50 630 m 370 630 l S\n" +
            "50 630 m 50 728 l S 150 630 m 150 728 l S 370 630 m 370 728 l S\n" +
            "0 0 0 rg\n" +
            "BT /F2 11 Tf 56 710 Td (Termin) Tj ET\n" +
            "BT /F2 11 Tf 156 710 Td (Vorbereitung) Tj ET\n" +
            "BT /F1 11 Tf 56 680 Td (1.: 18.03.2026) Tj ET\n" +
            "BT /F1 11 Tf 64 665 Td (08:00 Uhr,) Tj ET\n" +
            "BT /F1 11 Tf 64 650 Td (Raum A109) Tj ET\n" +
            "BT /F1 11 Tf 156 680 Td (Foliensatz I zur Hand ha-) Tj ET\n";

        var bytes = TestPdfGenerator.CreateSinglePagePdfFromStream(stream);
        var docService = new PdfDocumentService();
        var detector = new TextBlockDetector();
        var editor = new PdfTextEditor();
        var renderer = new PdfPageRenderer();

        using var session = await docService.OpenAsync(bytes, "table_edit.pdf");
        var layout1 = detector.DetectBlocks(session, 0);
        var terminBlock = layout1.Blocks.Single(b => b.Text == "Termin");
        var cellBlock = layout1.Blocks.Single(b => b.Text.Contains("Raum A109"));

        // Edit #1: edit the indented cell block
        var res1 = editor.ApplyTextEdit(session, new PdfTextEditRequest(
            OriginalBlock: cellBlock,
            NewText: "1.: 18.03.2026\n08:00 Uhr,\nRaum B202",
            FontFamily: cellBlock.PrimaryFontFamily,
            FontSize: cellBlock.FontSize,
            IsBold: cellBlock.IsBold,
            IsItalic: cellBlock.IsItalic,
            FillColor: cellBlock.PrimaryColor,
            Alignment: cellBlock.Alignment,
            LineSpacingRatio: cellBlock.LineSpacingRatio,
            LetterSpacingEm: cellBlock.LetterSpacingEm,
            TargetBounds: cellBlock.Bounds,
            AutoReflow: false));
        Assert.True(res1.Success);

        // Edit #2: edit the gray-shaded "Termin" header cell
        var res2 = editor.ApplyTextEdit(session, new PdfTextEditRequest(
            OriginalBlock: terminBlock,
            NewText: "Datum",
            FontFamily: terminBlock.PrimaryFontFamily,
            FontSize: terminBlock.FontSize,
            IsBold: terminBlock.IsBold,
            IsItalic: terminBlock.IsItalic,
            FillColor: terminBlock.PrimaryColor,
            Alignment: terminBlock.Alignment,
            LineSpacingRatio: terminBlock.LineSpacingRatio,
            LetterSpacingEm: terminBlock.LetterSpacingEm,
            TargetBounds: terminBlock.Bounds,
            AutoReflow: false));
        Assert.True(res2.Success);

        byte[] saved = session.SaveToBytes();
        using var reopened = await docService.OpenAsync(saved, "table_after.pdf");
        var layout2 = detector.DetectBlocks(reopened, 0);
        string allText = string.Join("\n", layout2.Blocks.Select(b => b.Text));

        Assert.DoesNotContain("Termin", allText);
        Assert.DoesNotContain("Raum A109", allText);
        Assert.Contains("Datum", allText);
        Assert.Contains("Vorbereitung", allText);
        Assert.Contains("Raum B202", allText);

        // Verify gray header cells stayed gray (~191,191,191) and did NOT turn black!
        using var bmp = await renderer.RenderPageAsync(reopened, 0, 612, 792);
        var headerPixel = bmp.GetPixel(220, 72); // inside "Vorbereitung" gray cell
        Assert.InRange(headerPixel.Red, 175, 205);
        Assert.InRange(headerPixel.Green, 175, 205);
        Assert.InRange(headerPixel.Blue, 175, 205);

        // Re-edit "Datum" -> "Tag" cleanly on the already-edited page
        var datumBlock = layout2.Blocks.Single(b => b.Text == "Datum");
        var res3 = editor.ApplyTextEdit(reopened, new PdfTextEditRequest(
            OriginalBlock: datumBlock,
            NewText: "Tag",
            FontFamily: datumBlock.PrimaryFontFamily,
            FontSize: datumBlock.FontSize,
            IsBold: datumBlock.IsBold,
            IsItalic: datumBlock.IsItalic,
            FillColor: datumBlock.PrimaryColor,
            Alignment: datumBlock.Alignment,
            LineSpacingRatio: datumBlock.LineSpacingRatio,
            LetterSpacingEm: datumBlock.LetterSpacingEm,
            TargetBounds: datumBlock.Bounds,
            AutoReflow: false));
        Assert.True(res3.Success);

        var layout3 = detector.DetectBlocks(reopened, 0);
        string allText3 = string.Join("\n", layout3.Blocks.Select(b => b.Text));
        Assert.DoesNotContain("Datum", allText3);
        Assert.Contains("Tag", allText3);
    }

    [Fact]
    public async Task ApplyTextEdit_WinAnsiGermanAndEuro_KeepsSavedPdfCompact()
    {
        const string stream = "BT /F1 11 Tf 72 700 Td (18.03.2026) Tj ET\n";
        var bytes = TestPdfGenerator.CreateSinglePagePdfFromStream(stream);
        var docService = new PdfDocumentService();
        var detector = new TextBlockDetector();
        var editor = new PdfTextEditor();
        var renderer = new PdfPageRenderer();

        using var session = await docService.OpenAsync(bytes, "compact.pdf");
        var layout = detector.DetectBlocks(session, 0);
        var block = Assert.Single(layout.Blocks);

        var res = editor.ApplyTextEdit(session, new PdfTextEditRequest(
            OriginalBlock: block,
            NewText: "19.03.2026 äöüß €",
            FontFamily: block.PrimaryFontFamily,
            FontSize: block.FontSize,
            IsBold: false,
            IsItalic: false,
            FillColor: block.PrimaryColor,
            Alignment: PdfTextAlignment.Left,
            LineSpacingRatio: 1.35,
            LetterSpacingEm: 0.0,
            TargetBounds: block.Bounds,
            AutoReflow: false));

        Assert.True(res.Success);
        byte[] saved = session.SaveToBytes();
        Assert.True(saved.Length < bytes.Length + 8192, $"Expected compact saved PDF, got {saved.Length} bytes.");

        using var reopened = await docService.OpenAsync(saved, "compact_after.pdf");
        var reopenedLayout = detector.DetectBlocks(reopened, 0);
        var editedBlock = Assert.Single(reopenedLayout.Blocks);
        Assert.Equal("19.03.2026 äöüß €", editedBlock.Text);
    }

    [Fact]
    public async Task ApplyTextEdit_ArialFont_PreservesArialAndReusesEmbeddedFontWithoutHelveticaSwitch()
    {
        const string stream = "BT /F1 11 Tf 72 700 Td (18.03.2026) Tj ET\n";
        var bytes = TestPdfGenerator.CreateSinglePagePdfFromStream(stream);
        var docService = new PdfDocumentService();
        var detector = new TextBlockDetector();
        var editor = new PdfTextEditor();
        var renderer = new PdfPageRenderer();

        // First edit sets the block to Arial explicitly
        using var initialSession = await docService.OpenAsync(bytes, "arial_src.pdf");
        var initialBlock = Assert.Single(detector.DetectBlocks(initialSession, 0).Blocks);
        var firstEdit = editor.ApplyTextEdit(initialSession, new PdfTextEditRequest(
            OriginalBlock: initialBlock,
            NewText: "19.03.2026",
            FontFamily: "Arial",
            FontSize: 11.0,
            IsBold: false,
            IsItalic: false,
            FillColor: initialBlock.PrimaryColor,
            Alignment: PdfTextAlignment.Left,
            LineSpacingRatio: 1.35,
            LetterSpacingEm: 0.0,
            TargetBounds: initialBlock.Bounds,
            AutoReflow: false));
        Assert.True(firstEdit.Success);

        byte[] arialPdfBytes = initialSession.SaveToBytes();
        using var reopenedArial = await docService.OpenAsync(arialPdfBytes, "arial_reopened.pdf");
        var arialBlock = Assert.Single(detector.DetectBlocks(reopenedArial, 0).Blocks);
        Assert.Contains("Arial", arialBlock.PrimaryFontFamily, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Helvetica", arialBlock.PrimaryFontFamily, StringComparison.OrdinalIgnoreCase);

        // Second edit on the Arial block must stay Arial (not turn into Helvetica) and reuse the existing font
        var secondEdit = editor.ApplyTextEdit(reopenedArial, new PdfTextEditRequest(
            OriginalBlock: arialBlock,
            NewText: "20.03.2026",
            FontFamily: arialBlock.PrimaryFontFamily,
            FontSize: arialBlock.FontSize,
            IsBold: arialBlock.IsBold,
            IsItalic: arialBlock.IsItalic,
            FillColor: arialBlock.PrimaryColor,
            Alignment: arialBlock.Alignment,
            LineSpacingRatio: arialBlock.LineSpacingRatio,
            LetterSpacingEm: arialBlock.LetterSpacingEm,
            TargetBounds: arialBlock.Bounds,
            AutoReflow: false));
        Assert.True(secondEdit.Success);

        byte[] afterSecondSave = reopenedArial.SaveToBytes();
        Assert.True(afterSecondSave.Length < arialPdfBytes.Length + 12288,
            $"Expected second Arial edit to reuse embedded font cleanly (before={arialPdfBytes.Length}, after={afterSecondSave.Length})");

        using var finalSession = await docService.OpenAsync(afterSecondSave, "arial_final.pdf");
        var finalBlock = Assert.Single(detector.DetectBlocks(finalSession, 0).Blocks);
        Assert.Equal("20.03.2026", finalBlock.Text);
        Assert.Equal(11.0, finalBlock.FontSize, precision: 1);
        Assert.InRange(finalBlock.Bounds.Left, initialBlock.Bounds.Left - 2, initialBlock.Bounds.Left + 2);
        Assert.InRange(finalBlock.Bounds.Top, initialBlock.Bounds.Top - 2, initialBlock.Bounds.Top + 2);
        Assert.InRange(finalBlock.Bounds.Width, 35, 90);
        Assert.Contains("Arial", finalBlock.PrimaryFontFamily, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Helvetica", finalBlock.PrimaryFontFamily, StringComparison.OrdinalIgnoreCase);

        using var bmp = await renderer.RenderPageAsync(finalSession, 0, 612, 792);
        int darkPixels = 0;
        for (int y = (int)finalBlock.Bounds.Top - 2; y <= (int)finalBlock.Bounds.Bottom + 2; y++)
        {
            for (int x = (int)finalBlock.Bounds.Left - 2; x <= (int)finalBlock.Bounds.Right + 2; x++)
            {
                var p = bmp.GetPixel(Math.Clamp(x, 0, 611), Math.Clamp(y, 0, 791));
                if (p.Red < 180 || p.Green < 180 || p.Blue < 180)
                {
                    darkPixels++;
                }
            }
        }

        Assert.True(darkPixels > 40, $"Expected crisp rendered dark text pixels for Arial edit, got {darkPixels}");
    }

    [Fact]
    public async Task ApplyTextEdit_EmbeddedArialInsideWrappedBasePage_RendersCrisplyAndPreservesArial()
    {
        // Build a PDF whose page ALREADY has an embedded Arial font (inside original page stream)
        var baseBytes = TestPdfGenerator.CreateSinglePagePdfFromStream("");
        var docService = new PdfDocumentService();
        var detector = new TextBlockDetector();
        var editor = new PdfTextEditor();
        var renderer = new PdfPageRenderer();

        byte[] sourcePdfWithArial;
        using (var prep = await docService.OpenAsync(baseBytes, "prep.pdf"))
        {
            var dummyBlock = new PdfTextBlock(
                Id: "init",
                PageIndex: 0,
                Kind: PdfBlockKind.Paragraph,
                Bounds: new PdfRect(72, 80, 140, 96),
                Text: "Init",
                PrimaryFontFamily: "Arial",
                FontWeightLabel: "Regular",
                FontSize: 12.0,
                IsBold: false,
                IsItalic: false,
                PrimaryColor: new PdfColor(0, 0, 0, 255),
                Alignment: PdfTextAlignment.Left,
                LineSpacingRatio: 1.35,
                LetterSpacingEm: 0.0,
                RotationDegrees: 0.0,
                IsEmbeddedFont: true,
                Lines: []);

            // Seed with initial Arial text so saved PDF has Arial on page 0
            editor.ApplyTextEdit(prep, new PdfTextEditRequest(
                OriginalBlock: dummyBlock,
                NewText: "Datum: 18.03.2026",
                FontFamily: "Arial",
                FontSize: 12.0,
                IsBold: false,
                IsItalic: false,
                FillColor: new PdfColor(0, 0, 0, 255),
                Alignment: PdfTextAlignment.Left,
                LineSpacingRatio: 1.35,
                LetterSpacingEm: 0.0,
                TargetBounds: dummyBlock.Bounds,
                AutoReflow: false));

            sourcePdfWithArial = prep.SaveToBytes();
        }

        // Now wrap/re-wrap like a freshly opened external PDF by opening and applying an edit
        // (note: remove PdfStudioBasePage mark simulation by checking both unwrapped and wrapped)
        using var session = await docService.OpenAsync(sourcePdfWithArial, "ext_arial.pdf");
        var block = Assert.Single(detector.DetectBlocks(session, 0).Blocks);
        var res = editor.ApplyTextEdit(session, new PdfTextEditRequest(
            OriginalBlock: block,
            NewText: "Datum: 25.04.2026",
            FontFamily: block.PrimaryFontFamily,
            FontSize: block.FontSize,
            IsBold: block.IsBold,
            IsItalic: block.IsItalic,
            FillColor: block.PrimaryColor,
            Alignment: block.Alignment,
            LineSpacingRatio: block.LineSpacingRatio,
            LetterSpacingEm: block.LetterSpacingEm,
            TargetBounds: block.Bounds,
            AutoReflow: false));

        Assert.True(res.Success);
        byte[] saved = session.SaveToBytes();
        using var reopened = await docService.OpenAsync(saved, "ext_arial_after.pdf");
        var afterBlock = Assert.Single(detector.DetectBlocks(reopened, 0).Blocks);
        Assert.Equal("Datum: 25.04.2026", afterBlock.Text);
        Assert.Contains("Arial", afterBlock.PrimaryFontFamily, StringComparison.OrdinalIgnoreCase);

        using var bmp = await renderer.RenderPageAsync(reopened, 0, 612, 792);
        int darkPixels = 0;
        for (int y = (int)afterBlock.Bounds.Top - 2; y <= (int)afterBlock.Bounds.Bottom + 2; y++)
        {
            for (int x = (int)afterBlock.Bounds.Left - 2; x <= (int)afterBlock.Bounds.Right + 2; x++)
            {
                var p = bmp.GetPixel(Math.Clamp(x, 0, 611), Math.Clamp(y, 0, 791));
                if (p.Red < 180 || p.Green < 180 || p.Blue < 180)
                {
                    darkPixels++;
                }
            }
        }

        Assert.True(darkPixels > 40, $"Expected dark pixels, got {darkPixels}");
    }
}
