using PdfStudio.Core.Documents;

namespace PdfStudio.Core.Tests;

public sealed class PdfDocumentServiceTests
{
    [Fact]
    public async Task OpenAsync_FromBytes_ReadsMetadataAndPageDimensions()
    {
        var bytes = TestPdfGenerator.CreateSampleTwoPagePdf();
        var service = new PdfDocumentService();

        using var session = await service.OpenAsync(bytes, "sample.pdf");

        Assert.Equal("sample.pdf", session.Metadata.FileName);
        Assert.Equal(2, session.Metadata.PageCount);
        Assert.Equal("PDF 1.7", session.Metadata.PdfVersion);
        Assert.Equal("Standard PDF Fonts", session.Metadata.FontEmbedSummary);

        Assert.Equal(2, session.Pages.Count);
        Assert.Equal(612, session.Pages[0].WidthPoints, precision: 2);
        Assert.Equal(792, session.Pages[0].HeightPoints, precision: 2);
        Assert.Equal(8.5, session.Pages[0].WidthInches, precision: 2);
        Assert.Equal(11.0, session.Pages[0].HeightInches, precision: 2);

        Assert.Equal(595, session.Pages[1].WidthPoints, precision: 2);
        Assert.Equal(842, session.Pages[1].HeightPoints, precision: 2);
    }

    [Fact]
    public async Task OpenAsync_FromFile_OpensAndCleansUp()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"pdfstudio_test_{Guid.NewGuid():N}.pdf");
        try
        {
            await File.WriteAllBytesAsync(tempPath, TestPdfGenerator.CreateSampleTwoPagePdf());
            var service = new PdfDocumentService();

            using var session = await service.OpenAsync(tempPath);

            Assert.Equal(tempPath, session.Metadata.FilePath);
            Assert.Equal(2, session.Pages.Count);
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
    public async Task OpenAsync_MissingFile_ThrowsFileNotFoundException()
    {
        var service = new PdfDocumentService();
        var missingPath = Path.Combine(Path.GetTempPath(), $"does_not_exist_{Guid.NewGuid():N}.pdf");

        await Assert.ThrowsAsync<FileNotFoundException>(() => service.OpenAsync(missingPath));
    }
}
