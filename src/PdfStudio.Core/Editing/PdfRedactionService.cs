using PDFiumCore;
using PdfStudio.Core.Documents;
using PdfStudio.Core.Layout;
using PdfStudio.Core.Pdfium;

namespace PdfStudio.Core.Editing;

public sealed record PdfRedactionResult(
    bool Success,
    int StrippedObjectCount,
    bool FlattenedFormXObject);

/// <summary>
/// Permanently strips covered PDF objects inside redaction regions and paints solid black redaction bars.
/// </summary>
public sealed class PdfRedactionService
{
    public PdfRedactionResult ApplyRedaction(PdfDocumentSession session, int pageIndex, PdfRect redactBounds)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (pageIndex < 0 || pageIndex >= session.Pages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        if (redactBounds.IsEmpty)
        {
            return new PdfRedactionResult(false, 0, false);
        }

        var pageInfo = session.Pages[pageIndex];
        double pageWidth = pageInfo.WidthPoints;
        double pageHeight = pageInfo.HeightPoints;

        return PdfiumEngine.Instance.RunLocked(() =>
        {
            var doc = session.Handle;
            var page = fpdfview.FPDF_LoadPage(doc, pageIndex);
            if (page is null || page.__Instance == IntPtr.Zero)
            {
                return new PdfRedactionResult(false, 0, false);
            }

            try
            {
                int stripped = StripCoveredTopLevelObjects(page, pageHeight, redactBounds);
                bool flattened = false;

                // If Form XObjects still contain readable text inside redactBounds, flatten & burn out the covered area permanently
                if (HasReadableTextInsideBounds(page, pageHeight, redactBounds))
                {
                    flattened = FlattenFormXObjectsWithRedactionBurnout(doc, page, pageWidth, pageHeight, redactBounds);
                    if (flattened)
                    {
                        stripped++;
                    }
                }

                // Draw permanent solid black redaction rectangle on top
                float rx = (float)redactBounds.Left;
                float ry = (float)(pageHeight - redactBounds.Bottom);
                float rw = (float)redactBounds.Width;
                float rh = (float)redactBounds.Height;

                var rectObj = fpdf_edit.FPDFPageObjCreateNewRect(rx, ry, rw, rh);
                if (rectObj is not null && rectObj.__Instance != IntPtr.Zero)
                {
                    fpdf_edit.FPDFPathSetDrawMode(rectObj, PdfPageLayerHelper.FpdfFillModeWinding, 0);
                    fpdf_edit.FPDFPageObjSetFillColor(rectObj, 0, 0, 0, 255);
                    fpdf_edit.FPDFPageObjAddMark(rectObj, PdfPageLayerHelper.MarkRedact);
                    fpdf_edit.FPDFPageInsertObject(page, rectObj);
                }

                bool ok = fpdf_edit.FPDFPageGenerateContent(page) != 0;
                return new PdfRedactionResult(ok, stripped, flattened);
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        });
    }

    private static int StripCoveredTopLevelObjects(FpdfPageT page, double pageHeight, PdfRect redactBounds)
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

            int type = fpdf_edit.FPDFPageObjGetType(obj);
            if (type != PdfPageLayerHelper.FpdfPageObjText && type != PdfPageLayerHelper.FpdfPageObjImage)
            {
                continue;
            }

            float l = 0, b = 0, r = 0, t = 0;
            if (fpdf_edit.FPDFPageObjGetBounds(obj, ref l, ref b, ref r, ref t) == 0)
            {
                continue;
            }

            var bounds = new PdfRect(
                Math.Min(l, r),
                Math.Min(pageHeight - t, pageHeight - b),
                Math.Max(l, r),
                Math.Max(pageHeight - t, pageHeight - b));

            if (IntersectsSignificantly(bounds, redactBounds))
            {
                toRemove.Add(obj);
            }
        }

        int stripped = 0;
        foreach (var obj in toRemove)
        {
            if (fpdf_edit.FPDFPageRemoveObject(page, obj) != 0)
            {
                fpdf_edit.FPDFPageObjDestroy(obj);
                stripped++;
            }
        }

        return stripped;
    }

    private static bool HasReadableTextInsideBounds(FpdfPageT page, double pageHeight, PdfRect redactBounds)
    {
        var textPage = fpdf_text.FPDFTextLoadPage(page);
        if (textPage is null || textPage.__Instance == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            int chars = fpdf_text.FPDFTextCountChars(textPage);
            for (int i = 0; i < chars; i++)
            {
                uint u = fpdf_text.FPDFTextGetUnicode(textPage, i);
                if (u == 0 || char.IsWhiteSpace((char)u))
                {
                    continue;
                }

                double l = 0, r = 0, b = 0, t = 0;
                if (fpdf_text.FPDFTextGetCharBox(textPage, i, ref l, ref r, ref b, ref t) == 0)
                {
                    continue;
                }

                var rect = new PdfRect(
                    Math.Min(l, r),
                    Math.Min(pageHeight - t, pageHeight - b),
                    Math.Max(l, r),
                    Math.Max(pageHeight - t, pageHeight - b));

                if (redactBounds.Contains(rect.CenterX, rect.CenterY, padding: 0.5))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            fpdf_text.FPDFTextClosePage(textPage);
        }
    }

    private static bool FlattenFormXObjectsWithRedactionBurnout(
        FpdfDocumentT doc,
        FpdfPageT page,
        double pageWidth,
        double pageHeight,
        PdfRect redactBounds)
    {
        // Remove all Form XObjects (which cannot be selectively stripped object-by-object via PDFium C API)
        // after rendering the page to a 200 DPI bitmap with the redacted region permanently burned to black.
        int objCount = fpdf_edit.FPDFPageCountObjects(page);
        var formObjects = new List<FpdfPageobjectT>();
        for (int i = 0; i < objCount; i++)
        {
            var obj = fpdf_edit.FPDFPageGetObject(page, i);
            if (obj is not null && obj.__Instance != IntPtr.Zero &&
                fpdf_edit.FPDFPageObjGetType(obj) == PdfPageLayerHelper.FpdfPageObjForm)
            {
                formObjects.Add(obj);
            }
        }

        if (formObjects.Count == 0)
        {
            return false;
        }

        const double dpiScale = 200.0 / 72.0;
        int bmpW = Math.Clamp((int)Math.Round(pageWidth * dpiScale), 64, 4096);
        int bmpH = Math.Clamp((int)Math.Round(pageHeight * dpiScale), 64, 4096);

        var bmp = fpdfview.FPDFBitmapCreate(bmpW, bmpH, 0);
        if (bmp is null || bmp.__Instance == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            fpdfview.FPDFBitmapFillRect(bmp, 0, 0, bmpW, bmpH, 0xFFFFFFFF);
            fpdfview.FPDF_RenderPageBitmap(bmp, page, 0, 0, bmpW, bmpH, 0, 0);

            int burnX = Math.Clamp((int)Math.Floor(redactBounds.Left * dpiScale), 0, bmpW - 1);
            int burnY = Math.Clamp((int)Math.Floor(redactBounds.Top * dpiScale), 0, bmpH - 1);
            int burnW = Math.Clamp((int)Math.Ceiling(redactBounds.Width * dpiScale), 1, bmpW - burnX);
            int burnH = Math.Clamp((int)Math.Ceiling(redactBounds.Height * dpiScale), 1, bmpH - burnY);
            fpdfview.FPDFBitmapFillRect(bmp, burnX, burnY, burnW, burnH, 0xFF000000);

            foreach (var formObj in formObjects)
            {
                if (fpdf_edit.FPDFPageRemoveObject(page, formObj) != 0)
                {
                    fpdf_edit.FPDFPageObjDestroy(formObj);
                }
            }

            var imgObj = fpdf_edit.FPDFPageObjNewImageObj(doc);
            if (imgObj is not null && imgObj.__Instance != IntPtr.Zero)
            {
                fpdf_edit.FPDFImageObjSetBitmap(page, 1, imgObj, bmp);
                fpdf_edit.FPDFImageObjSetMatrix(imgObj, pageWidth, 0, 0, pageHeight, 0, 0);
                fpdf_edit.FPDFPageInsertObject(page, imgObj);
                return true;
            }

            return false;
        }
        finally
        {
            fpdfview.FPDFBitmapDestroy(bmp);
        }
    }

    private static bool IntersectsSignificantly(PdfRect objBounds, PdfRect redactBounds)
    {
        if (redactBounds.Contains(objBounds.CenterX, objBounds.CenterY, padding: 1.0))
        {
            return true;
        }

        double interLeft = Math.Max(objBounds.Left, redactBounds.Left);
        double interTop = Math.Max(objBounds.Top, redactBounds.Top);
        double interRight = Math.Min(objBounds.Right, redactBounds.Right);
        double interBottom = Math.Min(objBounds.Bottom, redactBounds.Bottom);

        if (interRight <= interLeft || interBottom <= interTop)
        {
            return false;
        }

        double interArea = (interRight - interLeft) * (interBottom - interTop);
        double objArea = Math.Max(1.0, objBounds.Width * objBounds.Height);
        return (interArea / objArea) >= 0.45;
    }
}
