using PdfStudio.Core.Layout;

namespace PdfStudio.Core.Editing;

public sealed record PdfTextEditRequest(
    PdfTextBlock OriginalBlock,
    string NewText,
    string FontFamily,
    double FontSize,
    bool IsBold,
    bool IsItalic,
    PdfColor FillColor,
    PdfTextAlignment Alignment,
    double LineSpacingRatio,
    double LetterSpacingEm,
    PdfRect TargetBounds,
    bool AutoReflow);

public sealed record PdfTextEditResult(
    bool Success,
    bool UsedFallbackFont,
    string ResolvedFontFamily,
    int MissingGlyphCount,
    int RemovedObjectCount,
    int InsertedObjectCount,
    string? DiagnosticMessage = null);
