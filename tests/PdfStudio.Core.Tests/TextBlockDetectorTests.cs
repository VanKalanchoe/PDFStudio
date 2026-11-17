using PdfStudio.Core.Documents;
using PdfStudio.Core.Layout;

namespace PdfStudio.Core.Tests;

public sealed class TextBlockDetectorTests
{
    [Fact]
    public async Task DetectBlocks_GroupsHeadingAndMultiLineParagraphSeparately()
    {
        const string stream =
            "0.10 0.20 0.80 rg\n" +
            "BT /F2 22 Tf 72 700 Td (Q3 Financial Summary) Tj ET\n" +
            "0.0 0.0 0.0 rg\n" +
            "BT /F1 11 Tf 72 660 Td (Enterprise revenue accelerated across cloud licensing) Tj ET\n" +
            "BT /F1 11 Tf 72 644 Td (and subscription renewals during the fiscal quarter.) Tj ET\n" +
            "BT /F1 11 Tf 72 628 Td (Client retention remained strong across all regions.) Tj ET\n";

        var bytes = TestPdfGenerator.CreateSinglePagePdfFromStream(stream);
        var service = new PdfDocumentService();
        var detector = new TextBlockDetector();

        using var session = await service.OpenAsync(bytes, "layout.pdf");
        var layout = await detector.DetectBlocksAsync(session, 0);

        Assert.Equal(2, layout.Blocks.Count);

        var heading = layout.Blocks[0];
        Assert.Equal(PdfBlockKind.Heading, heading.Kind);
        Assert.Equal("Q3 Financial Summary", heading.Text);
        Assert.Equal(22.0, heading.FontSize, precision: 1);
        Assert.True(heading.IsBold);
        Assert.Equal("Helvetica", heading.PrimaryFontFamily);
        Assert.True(heading.PrimaryColor.B > heading.PrimaryColor.R);

        var paragraph = layout.Blocks[1];
        Assert.Equal(PdfBlockKind.Paragraph, paragraph.Kind);
        Assert.Equal(3, paragraph.Lines.Count);
        Assert.Equal(11.0, paragraph.FontSize, precision: 1);
        Assert.False(paragraph.IsBold);
        Assert.Contains("Enterprise revenue accelerated", paragraph.Text);
        Assert.Contains("Client retention remained strong", paragraph.Text);
        Assert.True(paragraph.LineSpacingRatio is > 1.2 and < 1.8);
    }

    [Fact]
    public async Task DetectBlocks_MultiColumnLayout_KeepsColumnsSeparate()
    {
        const string stream =
            // Left column (x = 54..240)
            "BT /F1 11 Tf 54 680 Td (Left column first line.) Tj ET\n" +
            "BT /F1 11 Tf 54 664 Td (Left column second line.) Tj ET\n" +
            // Right column at identical Y baselines (x = 340..540)
            "BT /F1 11 Tf 340 680 Td (Right column first line.) Tj ET\n" +
            "BT /F1 11 Tf 340 664 Td (Right column second line.) Tj ET\n";

        var bytes = TestPdfGenerator.CreateSinglePagePdfFromStream(stream);
        var service = new PdfDocumentService();
        var detector = new TextBlockDetector();

        using var session = await service.OpenAsync(bytes, "columns.pdf");
        var layout = await detector.DetectBlocksAsync(session, 0);

        Assert.Equal(2, layout.Blocks.Count);
        Assert.Contains("Left column first line.", layout.Blocks[0].Text);
        Assert.Contains("Left column second line.", layout.Blocks[0].Text);
        Assert.DoesNotContain("Right column", layout.Blocks[0].Text);

        Assert.Contains("Right column first line.", layout.Blocks[1].Text);
        Assert.Contains("Right column second line.", layout.Blocks[1].Text);
        Assert.DoesNotContain("Left column", layout.Blocks[1].Text);
    }

    [Fact]
    public async Task DetectBlocks_ItalicAndColoredBlocks_PreserveStyleMetadata()
    {
        const string stream =
            "0.84 0.31 0.33 rg\n" +
            "BT /F3 12 Tf 72 600 Td (Important italic notice) Tj ET\n";

        var bytes = TestPdfGenerator.CreateSinglePagePdfFromStream(stream);
        var service = new PdfDocumentService();
        var detector = new TextBlockDetector();

        using var session = await service.OpenAsync(bytes, "styled.pdf");
        var layout = await detector.DetectBlocksAsync(session, 0);

        var block = Assert.Single(layout.Blocks);
        Assert.True(block.IsItalic);
        Assert.Equal("Times", block.PrimaryFontFamily);
        Assert.InRange(block.PrimaryColor.R, 200, 225);
        Assert.InRange(block.PrimaryColor.G, 70, 90);
    }

    [Fact]
    public async Task DetectBlocks_EmptyPage_ReturnsNoBlocks()
    {
        const string stream = "0.2 0.4 0.8 rg 72 600 100 50 re f\n";
        var bytes = TestPdfGenerator.CreateSinglePagePdfFromStream(stream);
        var service = new PdfDocumentService();
        var detector = new TextBlockDetector();

        using var session = await service.OpenAsync(bytes, "graphics_only.pdf");
        var layout = await detector.DetectBlocksAsync(session, 0);

        Assert.Empty(layout.Blocks);
    }

    [Fact]
    public async Task DetectBlocks_MatrixScaledFontSize_ComputesTrueVisualPointSize()
    {
        const string stream = "BT /F1 1 Tf 14 0 0 14 72 680 Tm (17.11.2026) Tj ET\n";
        var bytes = TestPdfGenerator.CreateSinglePagePdfFromStream(stream);
        var service = new PdfDocumentService();
        var detector = new TextBlockDetector();

        using var session = await service.OpenAsync(bytes, "scaled_font.pdf");
        var layout = await detector.DetectBlocksAsync(session, 0);

        var block = Assert.Single(layout.Blocks);
        Assert.Equal("17.11.2026", block.Text);
        Assert.Equal(14.0, block.FontSize, precision: 1);
    }

    [Fact]
    public async Task DetectBlocks_TableWithVerticalAndHorizontalBorders_KeepsCellsSeparate()
    {
        // Tightly spaced table columns separated by vertical/horizontal black lines (gap ~10pt across border)
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
            "BT /F1 11 Tf 156 680 Td (Foliensatz I zur Hand ha-) Tj ET\n" +
            "BT /F1 11 Tf 156 665 Td (ben \\(ausgedruckt oder) Tj ET\n" +
            "BT /F1 11 Tf 156 650 Td (auf Mobilgeraet\\).) Tj ET\n";

        var bytes = TestPdfGenerator.CreateSinglePagePdfFromStream(stream);
        var service = new PdfDocumentService();
        var detector = new TextBlockDetector();

        using var session = await service.OpenAsync(bytes, "table.pdf");
        var layout = await detector.DetectBlocksAsync(session, 0);

        Assert.Equal(4, layout.Blocks.Count);
        Assert.Contains(layout.Blocks, b => b.Text == "Termin");
        Assert.Contains(layout.Blocks, b => b.Text == "Vorbereitung");
        Assert.Contains(layout.Blocks, b => b.Text.Contains("1.: 18.03.2026") && !b.Text.Contains("Foliensatz"));
        Assert.Contains(layout.Blocks, b => b.Text.Contains("Foliensatz I zur Hand") && b.Text.Contains("auf Mobilgeraet).") && !b.Text.Contains("Raum A109"));
    }
}
