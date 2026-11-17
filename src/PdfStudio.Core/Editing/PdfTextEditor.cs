using PDFiumCore;
using PdfStudio.Core.Documents;
using PdfStudio.Core.Layout;
using PdfStudio.Core.Pdfium;

namespace PdfStudio.Core.Editing;

/// <summary>
/// Commits in-place text edits non-destructively by preserving the original page stream inside a
/// Form XObject (<c>PdfStudioBasePage</c>), masking the edited region with the sampled cell background
/// (<c>PdfStudioMask</c>), and inserting shaped Unicode text objects (<c>PdfStudioEdit</c>).
/// </summary>
public sealed class PdfTextEditor
{
    private readonly FontFallbackResolver _fontResolver;

    public PdfTextEditor(FontFallbackResolver? fontResolver = null)
    {
        _fontResolver = fontResolver ?? new FontFallbackResolver();
    }

    public unsafe PdfTextEditResult ApplyTextEdit(PdfDocumentSession session, PdfTextEditRequest request)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);

        int pageIndex = request.OriginalBlock.PageIndex;
        if (pageIndex < 0 || pageIndex >= session.Pages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Target page index out of range.");
        }

        var pageInfo = session.Pages[pageIndex];
        double pageWidth = pageInfo.WidthPoints;
        double pageHeight = pageInfo.HeightPoints;

        double fontSize = Math.Clamp(request.FontSize, 4.0, 200.0);
        double lineSpacing = Math.Clamp(request.LineSpacingRatio, 0.8, 3.0);

        var wrappedLines = _fontResolver.WrapAndShapeText(
            text: request.NewText,
            preferredFamily: request.FontFamily,
            fontSize: fontSize,
            isBold: request.IsBold,
            isItalic: request.IsItalic,
            maxWidthPoints: request.TargetBounds.Width,
            autoReflow: request.AutoReflow,
            letterSpacingEm: request.LetterSpacingEm,
            out var coverage);

        PdfPageLayerHelper.EnsurePageWrapped(session, pageIndex, pageWidth, pageHeight);

        return PdfiumEngine.Instance.RunLocked(() =>
        {
            var doc = session.Handle;
            var page = fpdfview.FPDF_LoadPage(doc, pageIndex);
            if (page is null || page.__Instance == IntPtr.Zero)
            {
                throw new InvalidOperationException($"Failed to load PDF page {pageIndex + 1} for editing.");
            }

            var loadedFonts = new Dictionary<string, FpdfFontT>(StringComparer.OrdinalIgnoreCase);
            try
            {
                int removedCount = RemoveMatchingEditObjects(page, pageHeight, request.OriginalBlock.Bounds);

                if (!HasMaskCoveringBounds(page, pageHeight, request.OriginalBlock.Bounds) &&
                    !request.OriginalBlock.Bounds.IsEmpty)
                {
                    var bgColor = PdfPageLayerHelper.SamplePageBackgroundColorLocked(page, pageWidth, pageHeight, request.OriginalBlock.Bounds);
                    var b = request.OriginalBlock.Bounds;
                    float maskX = (float)Math.Max(0, b.Left - 0.8);
                    float maskY = (float)Math.Max(0, pageHeight - b.Bottom - 1.0);
                    float maskW = (float)(b.Width + 1.6);
                    float maskH = (float)(b.Height + 2.0);

                    var maskRect = fpdf_edit.FPDFPageObjCreateNewRect(maskX, maskY, maskW, maskH);
                    if (maskRect is not null && maskRect.__Instance != IntPtr.Zero)
                    {
                        fpdf_edit.FPDFPathSetDrawMode(maskRect, PdfPageLayerHelper.FpdfFillModeWinding, 0);
                        fpdf_edit.FPDFPageObjSetFillColor(maskRect, bgColor.R, bgColor.G, bgColor.B, 255);
                        fpdf_edit.FPDFPageObjAddMark(maskRect, PdfPageLayerHelper.MarkMask);
                        fpdf_edit.FPDFPageInsertObject(page, maskRect);
                    }
                }

                int insertedCount = 0;
                double baselineOffsetY = request.TargetBounds.Top - request.OriginalBlock.Bounds.Top;
                double deltaX = request.TargetBounds.Left - request.OriginalBlock.Bounds.Left;
                bool preservePerLineMetrics = wrappedLines.Count == request.OriginalBlock.Lines.Count
                                           && request.OriginalBlock.Lines.Count > 0
                                           && request.Alignment == request.OriginalBlock.Alignment;

                double firstLineBaseline = request.OriginalBlock.Lines.Count > 0
                    ? request.OriginalBlock.Lines[0].BaselineY + baselineOffsetY
                    : request.TargetBounds.Top + (fontSize * 0.85);
                double baseLeftX = (request.OriginalBlock.Lines.Count > 0
                    ? request.OriginalBlock.Lines[0].Bounds.Left
                    : request.TargetBounds.Left) + deltaX;
                var effectiveBounds = new PdfRect(
                    baseLeftX,
                    request.TargetBounds.Top,
                    baseLeftX + request.TargetBounds.Width,
                    request.TargetBounds.Bottom);

                double lineStep = fontSize * lineSpacing;
                double angleRad = request.OriginalBlock.RotationDegrees * (Math.PI / 180.0);
                float cos = (float)Math.Cos(angleRad);
                float sin = (float)Math.Sin(angleRad);

                for (int lineIdx = 0; lineIdx < wrappedLines.Count; lineIdx++)
                {
                    var line = wrappedLines[lineIdx];
                    if (line.Segments.Count == 0)
                    {
                        continue;
                    }

                    double topDownBaseline = preservePerLineMetrics
                        ? request.OriginalBlock.Lines[lineIdx].BaselineY + baselineOffsetY
                        : firstLineBaseline + (lineIdx * lineStep);

                    double pdfY = pageHeight - topDownBaseline;
                    double startX = preservePerLineMetrics
                        ? request.OriginalBlock.Lines[lineIdx].Bounds.Left + deltaX
                        : ComputeAlignedStartX(effectiveBounds, line.TotalWidthPoints, request.Alignment);

                    double cursorX = startX;
                    foreach (var segment in line.Segments)
                    {
                        if (string.IsNullOrEmpty(segment.Text))
                        {
                            continue;
                        }

                        var pdfFont = IsWinAnsiCompatible(segment.Text)
                            ? GetOrLoadStandardFont(doc, MapToStandard14Font(request.FontFamily, request.IsBold, request.IsItalic), loadedFonts)
                            : GetOrLoadPdfFont(doc, segment.Font, loadedFonts);
                        if (pdfFont is null || pdfFont.__Instance == IntPtr.Zero)
                        {
                            continue;
                        }

                        var textObj = fpdf_edit.FPDFPageObjCreateTextObj(doc, pdfFont, (float)fontSize);
                        if (textObj is null || textObj.__Instance == IntPtr.Zero)
                        {
                            continue;
                        }

                        var utf16 = (segment.Text + "\0").ToCharArray();
                        ushort[] codeUnits = new ushort[utf16.Length];
                        for (int k = 0; k < utf16.Length; k++)
                        {
                            codeUnits[k] = utf16[k];
                        }

                        if (fpdf_edit.FPDFTextSetText(textObj, ref codeUnits[0]) == 0)
                        {
                            fpdf_edit.FPDFPageObjDestroy(textObj);
                            continue;
                        }

                        using var matrix = new FS_MATRIX_
                        {
                            A = cos,
                            B = sin,
                            C = -sin,
                            D = cos,
                            E = (float)cursorX,
                            F = (float)pdfY
                        };

                        fpdf_edit.FPDFPageObjSetMatrix(textObj, matrix);
                        fpdf_edit.FPDFPageObjSetFillColor(
                            textObj,
                            request.FillColor.R,
                            request.FillColor.G,
                            request.FillColor.B,
                            request.FillColor.A);

                        fpdf_edit.FPDFPageObjAddMark(textObj, PdfPageLayerHelper.MarkEdit);
                        fpdf_edit.FPDFPageInsertObject(page, textObj);
                        insertedCount++;
                        cursorX += segment.WidthPoints;
                    }
                }

                if (fpdf_edit.FPDFPageGenerateContent(page) == 0)
                {
                    return new PdfTextEditResult(
                        Success: false,
                        UsedFallbackFont: coverage.UsedFallbackFont,
                        ResolvedFontFamily: coverage.SummaryFamilyName,
                        MissingGlyphCount: coverage.MissingGlyphCount,
                        RemovedObjectCount: removedCount,
                        InsertedObjectCount: insertedCount,
                        DiagnosticMessage: "FPDFPageGenerateContent returned false.");
                }

                return new PdfTextEditResult(
                    Success: true,
                    UsedFallbackFont: coverage.UsedFallbackFont,
                    ResolvedFontFamily: coverage.SummaryFamilyName,
                    MissingGlyphCount: coverage.MissingGlyphCount,
                    RemovedObjectCount: Math.Max(removedCount, 1),
                    InsertedObjectCount: insertedCount);
            }
            finally
            {
                foreach (var font in loadedFonts.Values)
                {
                    if (font.__Instance != IntPtr.Zero)
                    {
                        fpdf_edit.FPDFFontClose(font);
                    }
                }

                fpdfview.FPDF_ClosePage(page);
            }
        });
    }

    private static bool HasMaskCoveringBounds(FpdfPageT page, double pageHeight, PdfRect targetBounds)
    {
        int count = fpdf_edit.FPDFPageCountObjects(page);
        for (int i = 0; i < count; i++)
        {
            var obj = fpdf_edit.FPDFPageGetObject(page, i);
            if (obj is null || obj.__Instance == IntPtr.Zero)
            {
                continue;
            }

            if (fpdf_edit.FPDFPageObjGetType(obj) != PdfPageLayerHelper.FpdfPageObjPath ||
                !PdfPageLayerHelper.HasMark(obj, PdfPageLayerHelper.MarkMask))
            {
                continue;
            }

            float l = 0, b = 0, r = 0, t = 0;
            if (fpdf_edit.FPDFPageObjGetBounds(obj, ref l, ref b, ref r, ref t) == 0)
            {
                continue;
            }

            var rect = new PdfRect(
                Math.Min(l, r),
                Math.Min(pageHeight - t, pageHeight - b),
                Math.Max(l, r),
                Math.Max(pageHeight - t, pageHeight - b));

            if (rect.Contains(targetBounds.CenterX, targetBounds.CenterY, padding: 1.0))
            {
                return true;
            }
        }

        return false;
    }

    private static int RemoveMatchingEditObjects(
        FpdfPageT page,
        double pageHeight,
        PdfRect targetBlockBounds)
    {
        int count = fpdf_edit.FPDFPageCountObjects(page);
        var toRemove = new List<FpdfPageobjectT>();

        for (int i = 0; i < count; i++)
        {
            var obj = fpdf_edit.FPDFPageGetObject(page, i);
            if (obj is null || obj.__Instance == IntPtr.Zero)
            {
                continue;
            }

            if (fpdf_edit.FPDFPageObjGetType(obj) != PdfPageLayerHelper.FpdfPageObjText ||
                !PdfPageLayerHelper.HasMark(obj, PdfPageLayerHelper.MarkEdit))
            {
                continue;
            }

            float l = 0, b = 0, r = 0, t = 0;
            if (fpdf_edit.FPDFPageObjGetBounds(obj, ref l, ref b, ref r, ref t) == 0)
            {
                continue;
            }

            var topDownBounds = new PdfRect(
                Math.Min(l, r),
                Math.Min(pageHeight - t, pageHeight - b),
                Math.Max(l, r),
                Math.Max(pageHeight - t, pageHeight - b));

            if (targetBlockBounds.Contains(topDownBounds.CenterX, topDownBounds.CenterY, padding: 2.5))
            {
                toRemove.Add(obj);
            }
        }

        int removed = 0;
        foreach (var obj in toRemove)
        {
            if (fpdf_edit.FPDFPageRemoveObject(page, obj) != 0)
            {
                fpdf_edit.FPDFPageObjDestroy(obj);
                removed++;
            }
        }

        return removed;
    }

    private static FpdfFontT? GetOrLoadStandardFont(
        FpdfDocumentT doc,
        string standardFontName,
        Dictionary<string, FpdfFontT> cache)
    {
        string key = "__std14:" + standardFontName;
        if (cache.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var font = fpdf_edit.FPDFTextLoadStandardFont(doc, standardFontName);
        if (font is not null && font.__Instance != IntPtr.Zero)
        {
            cache[key] = font;
        }

        return font;
    }

    private static bool IsWinAnsiCompatible(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c <= 0x7E || (c >= 0xA0 && c <= 0xFF))
            {
                continue;
            }

            // Common WinAnsi 0x80..0x9F punctuation & Euro sign
            if (c is '\u20AC' or '\u201A' or '\u0192' or '\u201E' or '\u2026' or '\u2020' or '\u2021'
                or '\u02C6' or '\u2030' or '\u0160' or '\u2039' or '\u0152' or '\u017D'
                or '\u2018' or '\u2019' or '\u201C' or '\u201D' or '\u2022' or '\u2013' or '\u2014'
                or '\u02DC' or '\u2122' or '\u0161' or '\u203A' or '\u0153' or '\u017E' or '\u0178')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static string MapToStandard14Font(string family, bool isBold, bool isItalic)
    {
        string normalized = (family ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Contains("times") || normalized.Contains("georgia") ||
            normalized.Contains("cambria") || normalized.Contains("garamond") ||
            normalized.Contains("serif"))
        {
            return (isBold, isItalic) switch
            {
                (true, true) => "Times-BoldItalic",
                (true, false) => "Times-Bold",
                (false, true) => "Times-Italic",
                _ => "Times-Roman"
            };
        }

        if (normalized.Contains("courier") || normalized.Contains("consolas") ||
            normalized.Contains("mono") || normalized.Contains("cascadia"))
        {
            return (isBold, isItalic) switch
            {
                (true, true) => "Courier-BoldOblique",
                (true, false) => "Courier-Bold",
                (false, true) => "Courier-Oblique",
                _ => "Courier"
            };
        }

        return (isBold, isItalic) switch
        {
            (true, true) => "Helvetica-BoldOblique",
            (true, false) => "Helvetica-Bold",
            (false, true) => "Helvetica-Oblique",
            _ => "Helvetica"
        };
    }

    private static unsafe FpdfFontT? GetOrLoadPdfFont(
        FpdfDocumentT doc,
        ResolvedFontAsset asset,
        Dictionary<string, FpdfFontT> cache)
    {
        if (cache.TryGetValue(asset.FamilyName, out var existing))
        {
            return existing;
        }

        FpdfFontT? font = null;
        if (asset.FontBytes.Length > 0)
        {
            fixed (byte* ptr = asset.FontBytes)
            {
                font = fpdf_edit.FPDFTextLoadFont(
                    doc,
                    ptr,
                    (uint)asset.FontBytes.Length,
                    PdfPageLayerHelper.FpdfFontTrueType,
                    cid: 1);
            }
        }

        if (font is null || font.__Instance == IntPtr.Zero)
        {
            font = fpdf_edit.FPDFTextLoadStandardFont(doc, "Helvetica");
        }

        if (font is not null && font.__Instance != IntPtr.Zero)
        {
            cache[asset.FamilyName] = font;
        }

        return font;
    }

    private static double ComputeAlignedStartX(
        PdfRect bounds,
        double lineWidth,
        PdfTextAlignment alignment) => alignment switch
    {
        PdfTextAlignment.Center => bounds.Left + Math.Max(0, (bounds.Width - lineWidth) * 0.5),
        PdfTextAlignment.Right => bounds.Left + Math.Max(0, bounds.Width - lineWidth),
        _ => bounds.Left
    };
}
