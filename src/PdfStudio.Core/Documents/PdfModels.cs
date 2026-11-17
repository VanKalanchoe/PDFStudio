namespace PdfStudio.Core.Documents;

public sealed record PdfPageInfo(
    int PageIndex,
    int PageNumber,
    double WidthPoints,
    double HeightPoints,
    int RotationDegrees)
{
    public double WidthInches => WidthPoints / 72.0;
    public double HeightInches => HeightPoints / 72.0;
}

public sealed record PdfOutlineNode(
    string Title,
    int? TargetPageIndex,
    IReadOnlyList<PdfOutlineNode> Children);

public sealed record PdfDocumentMetadata(
    string? FilePath,
    string FileName,
    int PageCount,
    string PdfVersion,
    string FontEmbedSummary,
    string? Title,
    string? Author);

public sealed record PdfRenderOptions(
    bool RenderAnnotations = true,
    bool OptimizeTextForLcd = true,
    uint BackgroundArgb = 0xFFFFFFFF);
