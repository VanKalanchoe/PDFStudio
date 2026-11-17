namespace PdfStudio.Core.Layout;

public readonly record struct PdfColor(byte R, byte G, byte B, byte A = 255)
{
    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";
}

public readonly record struct PdfRect(double Left, double Top, double Right, double Bottom)
{
    public double Width => Math.Max(0, Right - Left);
    public double Height => Math.Max(0, Bottom - Top);
    public double CenterX => (Left + Right) * 0.5;
    public double CenterY => (Top + Bottom) * 0.5;
    public bool IsEmpty => Width <= 0.01 || Height <= 0.01;

    public bool Contains(double x, double y, double padding = 0) =>
        x >= Left - padding && x <= Right + padding && y >= Top - padding && y <= Bottom + padding;

    public PdfRect Union(PdfRect other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        return new PdfRect(
            Math.Min(Left, other.Left),
            Math.Min(Top, other.Top),
            Math.Max(Right, other.Right),
            Math.Max(Bottom, other.Bottom));
    }

    public double HorizontalOverlap(PdfRect other)
    {
        double overlap = Math.Min(Right, other.Right) - Math.Max(Left, other.Left);
        return Math.Max(0, overlap);
    }
}

public sealed record PdfGlyph(
    int CharIndex,
    char Character,
    PdfRect Bounds,
    PdfRect LooseBounds,
    double OriginX,
    double BaselineY,
    string FontFamily,
    string RawFontName,
    double FontSize,
    int FontWeight,
    int FontFlags,
    bool IsBold,
    bool IsItalic,
    double RotationDegrees,
    PdfColor FillColor,
    bool IsEmbeddedFont,
    bool PrecededBySpace = false);

public sealed record PdfTextSpan(
    string Text,
    PdfRect Bounds,
    string FontFamily,
    double FontSize,
    int FontWeight,
    bool IsBold,
    bool IsItalic,
    double RotationDegrees,
    PdfColor FillColor,
    bool IsEmbeddedFont,
    IReadOnlyList<PdfGlyph> Glyphs);

public sealed record PdfTextLine(
    string Text,
    PdfRect Bounds,
    double BaselineY,
    double FontSize,
    double RotationDegrees,
    IReadOnlyList<PdfTextSpan> Spans);

public enum PdfBlockKind
{
    Paragraph,
    Heading,
    HeaderFooter
}

public enum PdfTextAlignment
{
    Left,
    Center,
    Right,
    Justified
}

public sealed record PdfTextBlock(
    string Id,
    int PageIndex,
    PdfBlockKind Kind,
    PdfRect Bounds,
    string Text,
    string PrimaryFontFamily,
    string FontWeightLabel,
    double FontSize,
    bool IsBold,
    bool IsItalic,
    PdfColor PrimaryColor,
    PdfTextAlignment Alignment,
    double LineSpacingRatio,
    double LetterSpacingEm,
    double RotationDegrees,
    bool IsEmbeddedFont,
    IReadOnlyList<PdfTextLine> Lines);

public sealed record PdfPageLayout(
    int PageIndex,
    double PageWidthPoints,
    double PageHeightPoints,
    IReadOnlyList<PdfTextBlock> Blocks);
