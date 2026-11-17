using System.Runtime.InteropServices;
using System.Text;
using PDFiumCore;
using PdfStudio.Core.Documents;
using PdfStudio.Core.Pdfium;

namespace PdfStudio.Core.Layout;

/// <summary>
/// Reconstructs logical text spans, lines, headings, and multi-column paragraph blocks
/// from low-level PDFium glyph and page-object streams.
/// </summary>
public sealed class TextBlockDetector
{
    private const int FontFlagItalic = 1 << 6;
    private const int FontFlagForceBold = 1 << 18;
    private const int FpdfPageObjText = 1;
    private const int FpdfPageObjPath = 2;
    private const int FpdfPageObjForm = 5;

    private const string MarkBasePage = "PdfStudioBasePage";
    private const string MarkMask = "PdfStudioMask";
    private const string MarkEdit = "PdfStudioEdit";

    private readonly record struct VerticalDivider(double X, double Top, double Bottom);
    private readonly record struct HorizontalDivider(double Y, double Left, double Right);

    private sealed record PageTextObjInfo(
        PdfRect Bounds,
        PdfColor FillColor,
        double FontSize,
        int FontWeight,
        bool IsItalic);

    public PdfPageLayout DetectBlocks(PdfDocumentSession session, int pageIndex, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (pageIndex < 0 || pageIndex >= session.Pages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var pageInfo = session.Pages[pageIndex];
        double pageWidth = pageInfo.WidthPoints;
        double pageHeight = pageInfo.HeightPoints;

        var vDividers = new List<VerticalDivider>();
        var hDividers = new List<HorizontalDivider>();
        var glyphs = ExtractGlyphs(session, pageIndex, pageHeight, vDividers, hDividers, cancellationToken);
        if (glyphs.Count == 0)
        {
            return new PdfPageLayout(pageIndex, pageWidth, pageHeight, []);
        }

        var lines = GroupGlyphsIntoLines(glyphs, vDividers);
        var blocks = GroupLinesIntoBlocks(pageIndex, pageWidth, pageHeight, lines, vDividers, hDividers);

        return new PdfPageLayout(pageIndex, pageWidth, pageHeight, blocks);
    }

    public Task<PdfPageLayout> DetectBlocksAsync(
        PdfDocumentSession session,
        int pageIndex,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => DetectBlocks(session, pageIndex, cancellationToken), cancellationToken);
    }

    private static List<PdfGlyph> ExtractGlyphs(
        PdfDocumentSession session,
        int pageIndex,
        double pageHeight,
        List<VerticalDivider> vDividers,
        List<HorizontalDivider> hDividers,
        CancellationToken cancellationToken)
    {
        return PdfiumEngine.Instance.RunLocked(() =>
        {
            var doc = session.Handle;
            var page = fpdfview.FPDF_LoadPage(doc, pageIndex);
            if (page is null || page.__Instance == IntPtr.Zero)
            {
                return new List<PdfGlyph>();
            }

            try
            {
                var maskRects = new List<PdfRect>();
                var editTextObjects = new List<FpdfPageobjectT>();
                var pageTextObjects = new List<PageTextObjInfo>();

                CollectPageStructure(
                    page,
                    pageHeight,
                    maskRects,
                    editTextObjects,
                    pageTextObjects,
                    vDividers,
                    hDividers);

                var textPage = fpdf_text.FPDFTextLoadPage(page);
                if (textPage is null || textPage.__Instance == IntPtr.Zero)
                {
                    return new List<PdfGlyph>();
                }

                try
                {
                    int charCount = fpdf_text.FPDFTextCountChars(textPage);
                    int editStartCharIndex = ComputeEditStartCharIndex(textPage, charCount, maskRects, editTextObjects);
                    var glyphs = new List<PdfGlyph>(charCount);

                    using var looseRect = new FS_RECTF_();
                    using var textMatrix = new FS_MATRIX_();
                    int fontBufSize = 256;
                    IntPtr fontBuf = Marshal.AllocHGlobal(fontBufSize);
                    byte[] managedFontBuf = new byte[fontBufSize];
                    string? lastRawFontName = null;
                    int lastFontFlags = int.MinValue;
                    int lastRawWeight = int.MinValue;
                    bool lastObjItalic = false;
                    (string CleanFamily, int Weight, bool IsBold, bool IsItalic, bool IsEmbedded) lastNormalized =
                        ("Helvetica", 400, false, false, false);

                    bool pendingExplicitSpace = false;

                    try
                    {
                        for (int i = 0; i < charCount; i++)
                        {
                            if ((i & 127) == 0)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                            }

                            uint unicode = fpdf_text.FPDFTextGetUnicode(textPage, i);
                            if (unicode == 0 || unicode > 0xFFFF)
                            {
                                continue;
                            }

                            char ch = (char)unicode;
                            if (ch == '\u00AD')
                            {
                                ch = '-';
                            }

                            if (ch is '\r' or '\n' || char.IsControl(ch))
                            {
                                pendingExplicitSpace = false;
                                continue;
                            }

                            if (char.IsWhiteSpace(ch))
                            {
                                pendingExplicitSpace = true;
                                continue;
                            }

                            double left = 0, right = 0, bottom = 0, top = 0;
                            if (fpdf_text.FPDFTextGetCharBox(textPage, i, ref left, ref right, ref bottom, ref top) == 0)
                            {
                                continue;
                            }

                            var tightBounds = ToTopDownRect(left, right, bottom, top, pageHeight);
                            if (tightBounds.IsEmpty)
                            {
                                continue;
                            }

                            if (i < editStartCharIndex && IsMaskedOriginalGlyph(tightBounds, maskRects))
                            {
                                pendingExplicitSpace = false;
                                continue;
                            }

                            var looseBounds = ReadLooseCharBox(textPage, i, pageHeight, tightBounds, looseRect);

                            double originX = 0, originY = 0;
                            if (fpdf_text.FPDFTextGetCharOrigin(textPage, i, ref originX, ref originY) == 0)
                            {
                                originX = tightBounds.Left;
                                originY = bottom;
                            }

                            double baselineY = pageHeight - originY;
                            double fontSize = fpdf_text.FPDFTextGetFontSize(textPage, i);
                            if (fpdf_text.FPDFTextGetMatrix(textPage, i, textMatrix) != 0)
                            {
                                double sx = Math.Sqrt((textMatrix.A * textMatrix.A) + (textMatrix.B * textMatrix.B));
                                double sy = Math.Sqrt((textMatrix.C * textMatrix.C) + (textMatrix.D * textMatrix.D));
                                double matrixScale = Math.Max(sx, sy);
                                if (matrixScale > 0.01)
                                {
                                    fontSize *= matrixScale;
                                }
                            }

                            if (fontSize <= 0.1)
                            {
                                fontSize = Math.Max(6.0, tightBounds.Height);
                            }

                            var (rawFontName, fontFlags) = ReadFontInfoCached(
                                textPage,
                                i,
                                ref fontBuf,
                                ref fontBufSize,
                                ref managedFontBuf);

                            int fontWeight = fpdf_text.FPDFTextGetFontWeight(textPage, i);
                            if (fontWeight <= 0)
                            {
                                fontWeight = 400;
                            }

                            uint r = 0, g = 0, b = 0, a = 255;
                            bool hasFill = fpdf_text.FPDFTextGetFillColor(textPage, i, ref r, ref g, ref b, ref a) != 0;
                            var fillColor = hasFill
                                ? new PdfColor((byte)Math.Min(r, 255), (byte)Math.Min(g, 255), (byte)Math.Min(b, 255), (byte)Math.Min(a, 255))
                                : new PdfColor(0, 0, 0, 255);

                            bool objItalic = false;
                            if (!hasFill || fontWeight == 400)
                            {
                                var matchedObj = FindMatchingPageTextObject(pageTextObjects, tightBounds);
                                if (matchedObj is not null)
                                {
                                    if (!hasFill)
                                    {
                                        fillColor = matchedObj.FillColor;
                                    }

                                    if (fontWeight == 400 && matchedObj.FontWeight > 400)
                                    {
                                        fontWeight = matchedObj.FontWeight;
                                    }

                                    objItalic = matchedObj.IsItalic;
                                }
                            }

                            double angleRad = fpdf_text.FPDFTextGetCharAngle(textPage, i);
                            double angleDeg = Math.Round(angleRad * (180.0 / Math.PI), 1);

                            if (!string.Equals(rawFontName, lastRawFontName, StringComparison.Ordinal) ||
                                fontFlags != lastFontFlags ||
                                fontWeight != lastRawWeight ||
                                objItalic != lastObjItalic)
                            {
                                lastRawFontName = rawFontName;
                                lastFontFlags = fontFlags;
                                lastRawWeight = fontWeight;
                                lastObjItalic = objItalic;
                                lastNormalized = NormalizeFontMetadata(rawFontName, fontWeight, fontFlags, objItalic);
                            }

                            glyphs.Add(new PdfGlyph(
                                CharIndex: i,
                                Character: ch,
                                Bounds: tightBounds,
                                LooseBounds: looseBounds,
                                OriginX: originX,
                                BaselineY: baselineY,
                                FontFamily: lastNormalized.CleanFamily,
                                RawFontName: rawFontName,
                                FontSize: Math.Round(fontSize, 2),
                                FontWeight: lastNormalized.Weight,
                                FontFlags: fontFlags,
                                IsBold: lastNormalized.IsBold,
                                IsItalic: lastNormalized.IsItalic,
                                RotationDegrees: angleDeg,
                                FillColor: fillColor,
                                IsEmbeddedFont: lastNormalized.IsEmbedded,
                                PrecededBySpace: pendingExplicitSpace));
                            pendingExplicitSpace = false;
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(fontBuf);
                    }

                    return glyphs;
                }
                finally
                {
                    fpdf_text.FPDFTextClosePage(textPage);
                }
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        });
    }

    private static void CollectPageStructure(
        FpdfPageT page,
        double pageHeight,
        List<PdfRect> maskRects,
        List<FpdfPageobjectT> editTextObjects,
        List<PageTextObjInfo> pageTextObjects,
        List<VerticalDivider> vDividers,
        List<HorizontalDivider> hDividers)
    {
        int count = fpdf_edit.FPDFPageCountObjects(page);
        for (int i = 0; i < count; i++)
        {
            var obj = fpdf_edit.FPDFPageGetObject(page, i);
            if (obj is null || obj.__Instance == IntPtr.Zero)
            {
                continue;
            }

            CollectObjectRecursive(
                obj,
                pageHeight,
                parentMatrix: null,
                maskRects,
                editTextObjects,
                pageTextObjects,
                vDividers,
                hDividers,
                depth: 0);
        }
    }

    private static void CollectObjectRecursive(
        FpdfPageobjectT obj,
        double pageHeight,
        (float A, float B, float C, float D, float E, float F)? parentMatrix,
        List<PdfRect> maskRects,
        List<FpdfPageobjectT> editTextObjects,
        List<PageTextObjInfo> pageTextObjects,
        List<VerticalDivider> vDividers,
        List<HorizontalDivider> hDividers,
        int depth)
    {
        if (depth > 8)
        {
            return;
        }

        int objType = fpdf_edit.FPDFPageObjGetType(obj);
        if (objType == FpdfPageObjForm)
        {
            using var formMat = new FS_MATRIX_();
            var combined = parentMatrix;
            if (fpdf_edit.FPDFPageObjGetMatrix(obj, formMat) != 0)
            {
                var local = (formMat.A, formMat.B, formMat.C, formMat.D, formMat.E, formMat.F);
                combined = parentMatrix is null ? local : MultiplyMatrix(local, parentMatrix.Value);
            }

            int childCount = fpdf_edit.FPDFFormObjCountObjects(obj);
            for (int c = 0; c < childCount; c++)
            {
                var child = fpdf_edit.FPDFFormObjGetObject(obj, (uint)c);
                if (child is not null && child.__Instance != IntPtr.Zero)
                {
                    CollectObjectRecursive(
                        child,
                        pageHeight,
                        combined,
                        maskRects,
                        editTextObjects,
                        pageTextObjects,
                        vDividers,
                        hDividers,
                        depth + 1);
                }
            }

            return;
        }

        float l = 0, b = 0, r = 0, t = 0;
        if (fpdf_edit.FPDFPageObjGetBounds(obj, ref l, ref b, ref r, ref t) == 0)
        {
            return;
        }

        var bounds = parentMatrix is { } pm
            ? TransformToTopDownRect(l, b, r, t, pm, pageHeight)
            : ToTopDownRect(l, r, b, t, pageHeight);

        if (objType == FpdfPageObjPath)
        {
            if (depth == 0 && HasMark(obj, MarkMask))
            {
                maskRects.Add(bounds);
                return;
            }

            // Record thin vertical or horizontal table/divider lines
            if (bounds.Width <= 4.0 && bounds.Height >= 10.0)
            {
                vDividers.Add(new VerticalDivider(bounds.CenterX, bounds.Top, bounds.Bottom));
            }
            else if (bounds.Height <= 4.0 && bounds.Width >= 14.0)
            {
                hDividers.Add(new HorizontalDivider(bounds.CenterY, bounds.Left, bounds.Right));
            }

            return;
        }

        if (objType != FpdfPageObjText)
        {
            return;
        }

        if (depth == 0 && HasMark(obj, MarkEdit))
        {
            editTextObjects.Add(obj);
        }

        uint fr = 0, fg = 0, fb = 0, fa = 255;
        _ = fpdf_edit.FPDFPageObjGetFillColor(obj, ref fr, ref fg, ref fb, ref fa);
        var color = new PdfColor((byte)Math.Min(fr, 255), (byte)Math.Min(fg, 255), (byte)Math.Min(fb, 255), (byte)Math.Min(fa, 255));

        float size = 0;
        _ = fpdf_edit.FPDFTextObjGetFontSize(obj, ref size);

        int weight = 400;
        bool isItalic = false;
        var font = fpdf_edit.FPDFTextObjGetFont(obj);
        if (font is not null && font.__Instance != IntPtr.Zero)
        {
            int w = fpdf_edit.FPDFFontGetWeight(font);
            if (w > 0)
            {
                weight = w;
            }

            int italicAngle = 0;
            if (fpdf_edit.FPDFFontGetItalicAngle(font, ref italicAngle) != 0 && italicAngle != 0)
            {
                isItalic = true;
            }
        }

        pageTextObjects.Add(new PageTextObjInfo(bounds, color, size, weight, isItalic));
    }

    private static (float A, float B, float C, float D, float E, float F) MultiplyMatrix(
        (float A, float B, float C, float D, float E, float F) m1,
        (float A, float B, float C, float D, float E, float F) m2) =>
        (
            (m1.A * m2.A) + (m1.B * m2.C),
            (m1.A * m2.B) + (m1.B * m2.D),
            (m1.C * m2.A) + (m1.D * m2.C),
            (m1.C * m2.B) + (m1.D * m2.D),
            (m1.E * m2.A) + (m1.F * m2.C) + m2.E,
            (m1.E * m2.B) + (m1.F * m2.D) + m2.F
        );

    private static PdfRect TransformToTopDownRect(
        float l,
        float b,
        float r,
        float t,
        (float A, float B, float C, float D, float E, float F) m,
        double pageHeight)
    {
        (double x1, double y1) = ApplyMat(l, b, m);
        (double x2, double y2) = ApplyMat(r, t, m);
        (double x3, double y3) = ApplyMat(l, t, m);
        (double x4, double y4) = ApplyMat(r, b, m);

        double minX = Math.Min(Math.Min(x1, x2), Math.Min(x3, x4));
        double maxX = Math.Max(Math.Max(x1, x2), Math.Max(x3, x4));
        double minY = Math.Min(Math.Min(y1, y2), Math.Min(y3, y4));
        double maxY = Math.Max(Math.Max(y1, y2), Math.Max(y3, y4));

        return ToTopDownRect(minX, maxX, minY, maxY, pageHeight);
    }

    private static (double X, double Y) ApplyMat(double x, double y, (float A, float B, float C, float D, float E, float F) m) =>
        ((m.A * x) + (m.C * y) + m.E, (m.B * x) + (m.D * y) + m.F);

    private static bool HasMark(FpdfPageobjectT obj, string expectedName)
    {
        int markCount = fpdf_edit.FPDFPageObjCountMarks(obj);
        if (markCount <= 0)
        {
            return false;
        }

        byte[] managedBuf = new byte[128];
        IntPtr buf = Marshal.AllocHGlobal(128);
        try
        {
            for (int m = 0; m < markCount; m++)
            {
                var mark = fpdf_edit.FPDFPageObjGetMark(obj, (uint)m);
                if (mark is null || mark.__Instance == IntPtr.Zero)
                {
                    continue;
                }

                uint outLen = 0;
                if (fpdf_edit.FPDFPageObjMarkGetName(mark, buf, 128, ref outLen) != 0 && outLen > 2 && outLen <= 128)
                {
                    Marshal.Copy(buf, managedBuf, 0, (int)outLen);
                    string name = Encoding.Unicode.GetString(managedBuf, 0, (int)outLen).TrimEnd('\0');
                    if (string.Equals(name, expectedName, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static int ComputeEditStartCharIndex(
        FpdfTextpageT textPage,
        int totalChars,
        List<PdfRect> maskRects,
        List<FpdfPageobjectT> editTextObjects)
    {
        if (maskRects.Count == 0 || totalChars <= 0)
        {
            return totalChars;
        }

        if (editTextObjects.Count == 0)
        {
            return totalChars;
        }

        int editNonWsCount = 0;
        foreach (var obj in editTextObjects)
        {
            uint len = fpdf_edit.FPDFTextObjGetText(obj, textPage, ref UnsafeNullRef(), 0);
            if (len <= 2)
            {
                continue;
            }

            int ushortLen = (int)(len / 2);
            ushort[] buf = new ushort[ushortLen];
            uint written = fpdf_edit.FPDFTextObjGetText(obj, textPage, ref buf[0], len);
            int chars = Math.Min(ushortLen, (int)(written / 2));
            for (int k = 0; k < chars; k++)
            {
                char ch = (char)buf[k];
                if (ch != '\0' && !char.IsControl(ch) && !char.IsWhiteSpace(ch))
                {
                    editNonWsCount++;
                }
            }
        }

        if (editNonWsCount == 0)
        {
            return totalChars;
        }

        int seen = 0;
        for (int idx = totalChars - 1; idx >= 0; idx--)
        {
            uint u = fpdf_text.FPDFTextGetUnicode(textPage, idx);
            if (u == 0 || u > 0xFFFF)
            {
                continue;
            }

            char ch = (char)u;
            if (ch is '\r' or '\n' || char.IsControl(ch) || char.IsWhiteSpace(ch))
            {
                continue;
            }

            seen++;
            if (seen >= editNonWsCount)
            {
                return idx;
            }
        }

        return 0;
    }

    private static ref ushort UnsafeNullRef()
    {
        unsafe
        {
            return ref System.Runtime.CompilerServices.Unsafe.AsRef<ushort>(null);
        }
    }

    private static bool IsMaskedOriginalGlyph(PdfRect glyphBounds, List<PdfRect> maskRects)
    {
        if (maskRects.Count == 0)
        {
            return false;
        }

        double cx = glyphBounds.CenterX;
        double cy = glyphBounds.CenterY;
        for (int i = 0; i < maskRects.Count; i++)
        {
            if (maskRects[i].Contains(cx, cy, padding: 0.5))
            {
                return true;
            }
        }

        return false;
    }

    private static PageTextObjInfo? FindMatchingPageTextObject(List<PageTextObjInfo> objects, PdfRect glyphBounds)
    {
        double cx = glyphBounds.CenterX;
        double cy = glyphBounds.CenterY;

        for (int i = 0; i < objects.Count; i++)
        {
            if (objects[i].Bounds.Contains(cx, cy, padding: 2.0))
            {
                return objects[i];
            }
        }

        return null;
    }

    private static PdfRect ReadLooseCharBox(FpdfTextpageT textPage, int charIndex, double pageHeight, PdfRect fallback, FS_RECTF_ rect)
    {
        if (fpdf_text.FPDFTextGetLooseCharBox(textPage, charIndex, rect) == 0)
        {
            return fallback;
        }

        var converted = ToTopDownRect(rect.Left, rect.Right, rect.Bottom, rect.Top, pageHeight);
        return converted.IsEmpty ? fallback : converted;
    }

    private static (string FontName, int Flags) ReadFontInfoCached(
        FpdfTextpageT textPage,
        int charIndex,
        ref IntPtr buffer,
        ref int bufferSize,
        ref byte[] managedBuffer)
    {
        int flags = 0;
        uint required = fpdf_text.FPDFTextGetFontInfo(textPage, charIndex, buffer, (uint)bufferSize, ref flags);
        if (required <= 1)
        {
            return ("Helvetica", flags);
        }

        if (required > (uint)bufferSize)
        {
            bufferSize = (int)required + 64;
            Marshal.FreeHGlobal(buffer);
            buffer = Marshal.AllocHGlobal(bufferSize);
            managedBuffer = new byte[bufferSize];
            required = fpdf_text.FPDFTextGetFontInfo(textPage, charIndex, buffer, (uint)bufferSize, ref flags);
            if (required <= 1)
            {
                return ("Helvetica", flags);
            }
        }

        Marshal.Copy(buffer, managedBuffer, 0, (int)required);
        int len = (int)required;
        while (len > 0 && managedBuffer[len - 1] == 0)
        {
            len--;
        }

        string raw = len == 0 ? "Helvetica" : Encoding.UTF8.GetString(managedBuffer, 0, len).Trim();
        return (string.IsNullOrEmpty(raw) ? "Helvetica" : raw, flags);
    }

    private static PdfRect ToTopDownRect(double left, double right, double bottom, double top, double pageHeight)
    {
        double topDownTop = pageHeight - top;
        double topDownBottom = pageHeight - bottom;

        double minX = Math.Min(left, right);
        double maxX = Math.Max(left, right);
        double minY = Math.Min(topDownTop, topDownBottom);
        double maxY = Math.Max(topDownTop, topDownBottom);

        return new PdfRect(minX, minY, maxX, maxY);
    }

    private static (string CleanFamily, int Weight, bool IsBold, bool IsItalic, bool IsEmbedded) NormalizeFontMetadata(
        string rawName,
        int rawWeight,
        int fontFlags,
        bool objItalic)
    {
        bool isEmbedded = rawName.Contains('+') || !IsStandard14Font(rawName);

        // Strip PDF subset prefix like "ABCDEF+"
        string name = rawName;
        int plusIdx = name.IndexOf('+');
        if (plusIdx > 0 && plusIdx < name.Length - 1)
        {
            name = name[(plusIdx + 1)..];
        }

        bool isItalic = objItalic
                     || (fontFlags & FontFlagItalic) != 0
                     || name.Contains("Italic", StringComparison.OrdinalIgnoreCase)
                     || name.Contains("Oblique", StringComparison.OrdinalIgnoreCase);

        int weight = rawWeight;
        if (name.Contains("SemiBold", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("DemiBold", StringComparison.OrdinalIgnoreCase))
        {
            weight = Math.Max(weight, 600);
        }
        else if (name.Contains("Bold", StringComparison.OrdinalIgnoreCase) ||
                 (fontFlags & FontFlagForceBold) != 0)
        {
            weight = Math.Max(weight, 700);
        }
        else if (name.Contains("Medium", StringComparison.OrdinalIgnoreCase))
        {
            weight = Math.Max(weight, 500);
        }

        bool isBold = weight >= 600;

        // Clean style suffixes like "-BoldOblique", ",Bold", "-Regular"
        string cleanFamily = name;
        int sepIdx = cleanFamily.IndexOfAny(['-', ',']);
        if (sepIdx > 0)
        {
            cleanFamily = cleanFamily[..sepIdx];
        }

        cleanFamily = cleanFamily.Trim();
        if (string.IsNullOrEmpty(cleanFamily))
        {
            cleanFamily = "Helvetica";
        }

        return (cleanFamily, weight, isBold, isItalic, isEmbedded);
    }

    private static bool IsStandard14Font(string fontName) =>
        fontName is "Helvetica" or "Helvetica-Bold" or "Helvetica-Oblique" or "Helvetica-BoldOblique"
            or "Times-Roman" or "Times-Bold" or "Times-Italic" or "Times-BoldItalic"
            or "Courier" or "Courier-Bold" or "Courier-Oblique" or "Courier-BoldOblique"
            or "Symbol" or "ZapfDingbats";

    private static List<PdfTextLine> GroupGlyphsIntoLines(List<PdfGlyph> glyphs, List<VerticalDivider> vDividers)
    {
        // Bucket glyphs by compatible rotation + baseline Y
        var sorted = glyphs
            .OrderBy(g => Math.Round(g.BaselineY, 1))
            .ThenBy(g => g.Bounds.Left)
            .ToList();

        var baselineBuckets = new List<List<PdfGlyph>>();
        foreach (var glyph in sorted)
        {
            List<PdfGlyph>? targetBucket = null;
            for (int b = baselineBuckets.Count - 1; b >= 0; b--)
            {
                var bucket = baselineBuckets[b];
                var anchor = bucket[0];
                double tol = Math.Max(anchor.FontSize, glyph.FontSize) * 0.42;
                if (Math.Abs(glyph.BaselineY - anchor.BaselineY) <= tol &&
                    Math.Abs(glyph.RotationDegrees - anchor.RotationDegrees) <= 5.0)
                {
                    targetBucket = bucket;
                    break;
                }
            }

            if (targetBucket is null)
            {
                targetBucket = new List<PdfGlyph>();
                baselineBuckets.Add(targetBucket);
            }

            targetBucket.Add(glyph);
        }

        var lines = new List<PdfTextLine>();
        foreach (var bucket in baselineBuckets)
        {
            bucket.Sort((a, b) => a.Bounds.Left.CompareTo(b.Bounds.Left));

            // Split same-baseline glyphs into separate lines across column gutters or vertical border lines
            var lineSegments = new List<List<PdfGlyph>>();
            var currentSegment = new List<PdfGlyph> { bucket[0] };
            lineSegments.Add(currentSegment);

            for (int i = 1; i < bucket.Count; i++)
            {
                var prev = currentSegment[^1];
                var next = bucket[i];
                double refFont = Math.Max(prev.FontSize, next.FontSize);
                double gap = next.Bounds.Left - prev.Bounds.Right;
                double midY = (prev.BaselineY + next.BaselineY) * 0.5;

                bool separatedByBorder = HasVerticalDividerBetween(prev.Bounds.Right, next.Bounds.Left, midY, vDividers);

                // Table/column gap or vertical table border => separate logical line
                if (separatedByBorder || gap > Math.Max(refFont * 1.05, 9.0))
                {
                    currentSegment = new List<PdfGlyph> { next };
                    lineSegments.Add(currentSegment);
                }
                else
                {
                    currentSegment.Add(next);
                }
            }

            foreach (var segment in lineSegments)
            {
                lines.Add(BuildLineFromSegment(segment));
            }
        }

        return lines;
    }

    private static bool HasVerticalDividerBetween(
        double leftX,
        double rightX,
        double y,
        List<VerticalDivider> vDividers)
    {
        for (int i = 0; i < vDividers.Count; i++)
        {
            var v = vDividers[i];
            if (v.X >= leftX - 1.0 && v.X <= rightX + 1.0 && y >= v.Top - 3.0 && y <= v.Bottom + 3.0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasHorizontalDividerBetween(
        double topY,
        double bottomY,
        double leftX,
        double rightX,
        List<HorizontalDivider> hDividers)
    {
        for (int i = 0; i < hDividers.Count; i++)
        {
            var h = hDividers[i];
            if (h.Y >= topY - 1.0 && h.Y <= bottomY + 1.0 && h.Right >= leftX && h.Left <= rightX)
            {
                return true;
            }
        }

        return false;
    }

    private static PdfTextLine BuildLineFromSegment(List<PdfGlyph> segment)
    {
        var spans = new List<PdfTextSpan>();
        var currentSpanGlyphs = new List<PdfGlyph> { segment[0] };
        var spanText = new StringBuilder();
        spanText.Append(segment[0].Character);

        var lineText = new StringBuilder();
        lineText.Append(segment[0].Character);

        PdfRect lineBounds = segment[0].LooseBounds;
        double baselineSum = segment[0].BaselineY;
        double maxFontSize = segment[0].FontSize;

        for (int i = 1; i < segment.Count; i++)
        {
            var prev = segment[i - 1];
            var curr = segment[i];

            lineBounds = lineBounds.Union(curr.LooseBounds);
            baselineSum += curr.BaselineY;
            maxFontSize = Math.Max(maxFontSize, curr.FontSize);

            double refFont = Math.Max(prev.FontSize, curr.FontSize);
            double looseGap = curr.LooseBounds.Left - prev.LooseBounds.Right;
            double tightGap = curr.Bounds.Left - prev.Bounds.Right;
            bool insertSpace = curr.PrecededBySpace
                            || looseGap > refFont * 0.22
                            || tightGap > refFont * 0.44;

            bool sameStyle = string.Equals(prev.FontFamily, curr.FontFamily, StringComparison.OrdinalIgnoreCase)
                          && Math.Abs(prev.FontSize - curr.FontSize) <= 0.5
                          && prev.IsBold == curr.IsBold
                          && prev.IsItalic == curr.IsItalic
                          && prev.FillColor == curr.FillColor;

            if (insertSpace)
            {
                lineText.Append(' ');
            }

            lineText.Append(curr.Character);

            if (sameStyle)
            {
                if (insertSpace)
                {
                    spanText.Append(' ');
                }

                spanText.Append(curr.Character);
                currentSpanGlyphs.Add(curr);
            }
            else
            {
                spans.Add(CreateSpan(spanText.ToString(), currentSpanGlyphs));
                spanText.Clear();
                spanText.Append(curr.Character);
                currentSpanGlyphs = [curr];
            }
        }

        if (currentSpanGlyphs.Count > 0)
        {
            spans.Add(CreateSpan(spanText.ToString(), currentSpanGlyphs));
        }

        double avgBaseline = baselineSum / segment.Count;
        return new PdfTextLine(
            Text: lineText.ToString(),
            Bounds: lineBounds,
            BaselineY: avgBaseline,
            FontSize: Math.Round(maxFontSize, 2),
            RotationDegrees: segment[0].RotationDegrees,
            Spans: spans);
    }

    private static PdfTextSpan CreateSpan(string text, List<PdfGlyph> glyphs)
    {
        var first = glyphs[0];
        var bounds = first.LooseBounds;
        for (int i = 1; i < glyphs.Count; i++)
        {
            bounds = bounds.Union(glyphs[i].LooseBounds);
        }

        return new PdfTextSpan(
            Text: text,
            Bounds: bounds,
            FontFamily: first.FontFamily,
            FontSize: first.FontSize,
            FontWeight: first.FontWeight,
            IsBold: first.IsBold,
            IsItalic: first.IsItalic,
            RotationDegrees: first.RotationDegrees,
            FillColor: first.FillColor,
            IsEmbeddedFont: first.IsEmbeddedFont,
            Glyphs: glyphs.ToArray());
    }

    private static List<PdfTextBlock> GroupLinesIntoBlocks(
        int pageIndex,
        double pageWidth,
        double pageHeight,
        List<PdfTextLine> lines,
        List<VerticalDivider> vDividers,
        List<HorizontalDivider> hDividers)
    {
        // Order lines top-to-bottom then left-to-right so we can attach each line to an active column block
        var orderedLines = lines
            .OrderBy(l => l.BaselineY)
            .ThenBy(l => l.Bounds.Left)
            .ToList();

        var activeGroups = new List<List<PdfTextLine>>();

        foreach (var line in orderedLines)
        {
            List<PdfTextLine>? bestGroup = null;
            double bestScore = double.MaxValue;

            foreach (var group in activeGroups)
            {
                var last = group[^1];
                if (!CanMergeLines(last, line, vDividers, hDividers))
                {
                    continue;
                }

                double verticalDist = line.BaselineY - last.BaselineY;
                double leftDiff = Math.Abs(line.Bounds.Left - last.Bounds.Left);
                double score = verticalDist + (leftDiff * 0.25);

                if (score < bestScore)
                {
                    bestScore = score;
                    bestGroup = group;
                }
            }

            if (bestGroup is null)
            {
                activeGroups.Add([line]);
            }
            else
            {
                bestGroup.Add(line);
            }
        }

        var blocks = new List<PdfTextBlock>(activeGroups.Count);
        for (int i = 0; i < activeGroups.Count; i++)
        {
            blocks.Add(BuildTextBlock(pageIndex, i, pageHeight, activeGroups[i]));
        }

        // Reading-order sort: cluster columns left-to-right when blocks do not vertically overlap,
        // or top-to-bottom within the same column.
        return blocks
            .OrderBy(b => GetColumnBucket(b, pageWidth))
            .ThenBy(b => b.Bounds.Top)
            .ThenBy(b => b.Bounds.Left)
            .ToList();
    }

    private static int GetColumnBucket(PdfTextBlock block, double pageWidth)
    {
        // Wide blocks (spanning across center of page) sort globally top-to-bottom with bucket 0
        if (block.Bounds.Width > pageWidth * 0.55)
        {
            return 0;
        }

        return (int)(block.Bounds.Left / Math.Max(120, pageWidth * 0.35));
    }

    private static bool CanMergeLines(
        PdfTextLine prev,
        PdfTextLine next,
        List<VerticalDivider> vDividers,
        List<HorizontalDivider> hDividers)
    {
        if (Math.Abs(prev.RotationDegrees - next.RotationDegrees) > 5.0)
        {
            return false;
        }

        double maxFont = Math.Max(prev.FontSize, next.FontSize);
        double minFont = Math.Min(prev.FontSize, next.FontSize);
        if (Math.Abs(maxFont - minFont) > Math.Max(0.8, minFont * 0.08))
        {
            return false;
        }

        double baselineDelta = next.BaselineY - prev.BaselineY;
        if (baselineDelta < minFont * 0.55 || baselineDelta > maxFont * 1.95)
        {
            return false;
        }

        // Never merge across table row or column border lines
        double minLeft = Math.Min(prev.Bounds.Left, next.Bounds.Left);
        double maxRight = Math.Max(prev.Bounds.Right, next.Bounds.Right);
        if (HasHorizontalDividerBetween(prev.Bounds.CenterY, next.Bounds.CenterY, minLeft, maxRight, hDividers))
        {
            return false;
        }

        if (prev.Bounds.Right < next.Bounds.Left &&
            HasVerticalDividerBetween(prev.Bounds.Right, next.Bounds.Left, next.BaselineY, vDividers))
        {
            return false;
        }

        if (next.Bounds.Right < prev.Bounds.Left &&
            HasVerticalDividerBetween(next.Bounds.Right, prev.Bounds.Left, next.BaselineY, vDividers))
        {
            return false;
        }

        // Style discontinuity: bold heading or label above regular body text should stay distinct
        var prevSpan = prev.Spans[0];
        var nextSpan = next.Spans[0];
        if (!string.Equals(prevSpan.FontFamily, nextSpan.FontFamily, StringComparison.OrdinalIgnoreCase) ||
            prevSpan.IsBold != nextSpan.IsBold ||
            prevSpan.IsItalic != nextSpan.IsItalic ||
            prevSpan.FillColor != nextSpan.FillColor)
        {
            return false;
        }

        // Check horizontal alignment / column membership
        double overlap = prev.Bounds.HorizontalOverlap(next.Bounds);
        double minWidth = Math.Min(prev.Bounds.Width, next.Bounds.Width);
        bool hasOverlap = minWidth > 0 && (overlap / minWidth) >= 0.25;
        bool leftAligned = Math.Abs(prev.Bounds.Left - next.Bounds.Left) <= maxFont * 2.0;
        bool centerAligned = Math.Abs(prev.Bounds.CenterX - next.Bounds.CenterX) <= maxFont * 2.0;

        if (!hasOverlap && !leftAligned && !centerAligned)
        {
            return false;
        }

        // Prevent merging a short bold standalone heading into a paragraph below
        if (prevSpan.IsBold && !nextSpan.IsBold && prev.Text.Length < 60 && baselineDelta > maxFont * 1.35)
        {
            return false;
        }

        return true;
    }

    private static PdfTextBlock BuildTextBlock(
        int pageIndex,
        int index,
        double pageHeight,
        List<PdfTextLine> lines)
    {
        PdfRect bounds = lines[0].Bounds;
        var textBuilder = new StringBuilder();

        for (int i = 0; i < lines.Count; i++)
        {
            bounds = bounds.Union(lines[i].Bounds);
            if (i > 0)
            {
                textBuilder.Append('\n');
            }

            textBuilder.Append(lines[i].Text);
        }

        var allSpans = lines.SelectMany(l => l.Spans).ToList();
        var primarySpan = allSpans
            .GroupBy(s => (s.FontFamily, s.FontSize, s.FontWeight, s.IsBold, s.IsItalic, s.FillColor, s.IsEmbeddedFont))
            .OrderByDescending(g => g.Sum(x => x.Text.Length))
            .First()
            .Key;

        double lineSpacingRatio = EstimateLineSpacingRatio(lines, primarySpan.FontSize);
        double letterSpacingEm = EstimateLetterSpacingEm(lines, primarySpan.FontSize);
        var alignment = InferAlignment(lines, bounds);

        PdfBlockKind kind = PdfBlockKind.Paragraph;
        if (primarySpan.FontSize >= 14.5 || (lines.Count <= 2 && primarySpan.IsBold && primarySpan.FontSize >= 12.0))
        {
            kind = PdfBlockKind.Heading;
        }
        else if (lines.Count == 1 && (bounds.Top < pageHeight * 0.08 || bounds.Bottom > pageHeight * 0.92))
        {
            kind = PdfBlockKind.HeaderFooter;
        }

        string weightLabel = primarySpan.FontWeight switch
        {
            >= 700 => "Bold",
            >= 600 => "SemiBold",
            >= 500 => "Medium",
            _ => "Regular"
        };

        return new PdfTextBlock(
            Id: $"p{pageIndex}-b{index}",
            PageIndex: pageIndex,
            Kind: kind,
            Bounds: bounds,
            Text: textBuilder.ToString(),
            PrimaryFontFamily: primarySpan.FontFamily,
            FontWeightLabel: weightLabel,
            FontSize: Math.Round(primarySpan.FontSize, 1),
            IsBold: primarySpan.IsBold,
            IsItalic: primarySpan.IsItalic,
            PrimaryColor: primarySpan.FillColor,
            Alignment: alignment,
            LineSpacingRatio: lineSpacingRatio,
            LetterSpacingEm: letterSpacingEm,
            RotationDegrees: lines[0].RotationDegrees,
            IsEmbeddedFont: primarySpan.IsEmbeddedFont,
            Lines: lines);
    }

    private static double EstimateLineSpacingRatio(List<PdfTextLine> lines, double fontSize)
    {
        if (lines.Count < 2 || fontSize <= 0.1)
        {
            return 1.35;
        }

        var ratios = new List<double>();
        for (int i = 1; i < lines.Count; i++)
        {
            double delta = lines[i].BaselineY - lines[i - 1].BaselineY;
            if (delta > 0)
            {
                ratios.Add(delta / fontSize);
            }
        }

        if (ratios.Count == 0)
        {
            return 1.35;
        }

        ratios.Sort();
        return Math.Round(ratios[ratios.Count / 2], 2);
    }

    private static double EstimateLetterSpacingEm(List<PdfTextLine> lines, double fontSize)
    {
        if (fontSize <= 0.1)
        {
            return 0.0;
        }

        double totalRatio = 0;
        int samples = 0;
        foreach (var line in lines)
        {
            foreach (var span in line.Spans)
            {
                for (int i = 1; i < span.Glyphs.Count; i++)
                {
                    double gap = span.Glyphs[i].Bounds.Left - span.Glyphs[i - 1].Bounds.Right;
                    if (gap >= -fontSize * 0.1 && gap <= fontSize * 0.22)
                    {
                        totalRatio += gap / fontSize;
                        samples++;
                    }
                }
            }
        }

        if (samples < 3)
        {
            return 0.0;
        }

        double avg = totalRatio / samples;
        return Math.Abs(avg) < 0.08 ? 0.0 : Math.Round(avg, 2);
    }

    private static PdfTextAlignment InferAlignment(List<PdfTextLine> lines, PdfRect blockBounds)
    {
        if (lines.Count < 2)
        {
            return PdfTextAlignment.Left;
        }

        double leftSpread = lines.Max(l => Math.Abs(l.Bounds.Left - blockBounds.Left));
        double rightSpread = lines.Take(lines.Count - 1).Max(l => Math.Abs(l.Bounds.Right - blockBounds.Right));
        double centerSpread = lines.Max(l => Math.Abs(l.Bounds.CenterX - blockBounds.CenterX));

        if (leftSpread <= 2.5 && rightSpread <= 3.5)
        {
            return PdfTextAlignment.Justified;
        }

        // Hanging indents have multiple lines sharing the exact same indented left margin; don't misclassify as Center
        bool hasSharedLeftMargin = lines.Count(l => Math.Abs(l.Bounds.Left - lines[^1].Bounds.Left) <= 1.5) >= 2;
        if (!hasSharedLeftMargin && centerSpread <= 2.5 && leftSpread > centerSpread * 1.8)
        {
            return PdfTextAlignment.Center;
        }

        if (rightSpread < leftSpread && rightSpread <= 2.5)
        {
            return PdfTextAlignment.Right;
        }

        return PdfTextAlignment.Left;
    }
}
