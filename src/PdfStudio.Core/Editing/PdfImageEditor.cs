using PDFiumCore;
using PdfStudio.Core.Documents;
using PdfStudio.Core.Layout;
using PdfStudio.Core.Pdfium;
using SkiaSharp;

namespace PdfStudio.Core.Editing;

public sealed record PdfImageObjectInfo(
    int PageIndex,
    int ObjectIndex,
    PdfRect Bounds);

/// <summary>
/// Inserts, lists, moves, resizes, and removes raster images on PDF pages via PDFium <c>FPDFImageObj_*</c>.
/// </summary>
public sealed class PdfImageEditor
{
    private const int FpdfBitmapBgra = 4;

    public bool InsertImage(PdfDocumentSession session, int pageIndex, byte[] imageBytes, PdfRect targetBounds)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(imageBytes);
        if (pageIndex < 0 || pageIndex >= session.Pages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        using var decoded = SKBitmap.Decode(imageBytes)
            ?? throw new InvalidOperationException("Unsupported or corrupted image file.");

        using var bgraBitmap = ConvertToBgra8888(decoded);

        var pageInfo = session.Pages[pageIndex];
        double pageWidth = pageInfo.WidthPoints;
        double pageHeight = pageInfo.HeightPoints;

        PdfPageLayerHelper.EnsurePageWrapped(session, pageIndex, pageWidth, pageHeight);

        return PdfiumEngine.Instance.RunLocked(() =>
        {
            var doc = session.Handle;
            var page = fpdfview.FPDF_LoadPage(doc, pageIndex);
            if (page is null || page.__Instance == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                var imgObj = fpdf_edit.FPDFPageObjNewImageObj(doc);
                if (imgObj is null || imgObj.__Instance == IntPtr.Zero)
                {
                    return false;
                }

                var fpdfBitmap = fpdfview.FPDFBitmapCreateEx(
                    bgraBitmap.Width,
                    bgraBitmap.Height,
                    FpdfBitmapBgra,
                    bgraBitmap.GetPixels(),
                    bgraBitmap.RowBytes);

                if (fpdfBitmap is null || fpdfBitmap.__Instance == IntPtr.Zero)
                {
                    fpdf_edit.FPDFPageObjDestroy(imgObj);
                    return false;
                }

                try
                {
                    if (fpdf_edit.FPDFImageObjSetBitmap(page, 1, imgObj, fpdfBitmap) == 0)
                    {
                        fpdf_edit.FPDFPageObjDestroy(imgObj);
                        return false;
                    }
                }
                finally
                {
                    fpdfview.FPDFBitmapDestroy(fpdfBitmap);
                }

                double pdfBottomY = pageHeight - targetBounds.Bottom;
                fpdf_edit.FPDFImageObjSetMatrix(
                    imgObj,
                    targetBounds.Width,
                    0,
                    0,
                    targetBounds.Height,
                    targetBounds.Left,
                    pdfBottomY);

                fpdf_edit.FPDFPageObjAddMark(imgObj, PdfPageLayerHelper.MarkImage);
                fpdf_edit.FPDFPageInsertObject(page, imgObj);
                return fpdf_edit.FPDFPageGenerateContent(page) != 0;
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        });
    }

    public IReadOnlyList<PdfImageObjectInfo> GetImages(PdfDocumentSession session, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (pageIndex < 0 || pageIndex >= session.Pages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        var pageInfo = session.Pages[pageIndex];
        double pageWidth = pageInfo.WidthPoints;
        double pageHeight = pageInfo.HeightPoints;

        return PdfiumEngine.Instance.RunLocked(() =>
        {
            var result = new List<PdfImageObjectInfo>();
            var page = fpdfview.FPDF_LoadPage(session.Handle, pageIndex);
            if (page is null || page.__Instance == IntPtr.Zero)
            {
                return (IReadOnlyList<PdfImageObjectInfo>)result;
            }

            try
            {
                int count = fpdf_edit.FPDFPageCountObjects(page);
                for (int i = 0; i < count; i++)
                {
                    var obj = fpdf_edit.FPDFPageGetObject(page, i);
                    if (obj is null || obj.__Instance == IntPtr.Zero ||
                        fpdf_edit.FPDFPageObjGetType(obj) != PdfPageLayerHelper.FpdfPageObjImage)
                    {
                        continue;
                    }

                    float l = 0, b = 0, r = 0, t = 0;
                    if (fpdf_edit.FPDFPageObjGetBounds(obj, ref l, ref b, ref r, ref t) == 0 ||
                        r <= l || t <= b)
                    {
                        continue;
                    }

                    var bounds = new PdfRect(l, pageHeight - t, r, pageHeight - b);
                    bool isUserImage = PdfPageLayerHelper.HasMark(obj, PdfPageLayerHelper.MarkImage);
                    bool isFullPageBackground = !isUserImage
                        && bounds.Width >= pageWidth * 0.94
                        && bounds.Height >= pageHeight * 0.94;
                    if (isFullPageBackground)
                    {
                        continue;
                    }

                    result.Add(new PdfImageObjectInfo(pageIndex, i, bounds));
                }

                return result;
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        });
    }

    public bool SetImageBounds(PdfDocumentSession session, int pageIndex, int objectIndex, PdfRect newBounds)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (pageIndex < 0 || pageIndex >= session.Pages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        double pageHeight = session.Pages[pageIndex].HeightPoints;
        double width = Math.Max(8.0, newBounds.Width);
        double height = Math.Max(8.0, newBounds.Height);
        double pdfBottomY = pageHeight - (newBounds.Top + height);

        return PdfiumEngine.Instance.RunLocked(() =>
        {
            var page = fpdfview.FPDF_LoadPage(session.Handle, pageIndex);
            if (page is null || page.__Instance == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                if (objectIndex < 0 || objectIndex >= fpdf_edit.FPDFPageCountObjects(page))
                {
                    return false;
                }

                var obj = fpdf_edit.FPDFPageGetObject(page, objectIndex);
                if (obj is null || obj.__Instance == IntPtr.Zero ||
                    fpdf_edit.FPDFPageObjGetType(obj) != PdfPageLayerHelper.FpdfPageObjImage)
                {
                    return false;
                }

                if (fpdf_edit.FPDFImageObjSetMatrix(obj, width, 0, 0, height, newBounds.Left, pdfBottomY) == 0)
                {
                    return false;
                }

                return fpdf_edit.FPDFPageGenerateContent(page) != 0;
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        });
    }

    public bool DeleteImage(PdfDocumentSession session, int pageIndex, int objectIndex)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (pageIndex < 0 || pageIndex >= session.Pages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        return PdfiumEngine.Instance.RunLocked(() =>
        {
            var page = fpdfview.FPDF_LoadPage(session.Handle, pageIndex);
            if (page is null || page.__Instance == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                if (objectIndex < 0 || objectIndex >= fpdf_edit.FPDFPageCountObjects(page))
                {
                    return false;
                }

                var obj = fpdf_edit.FPDFPageGetObject(page, objectIndex);
                if (obj is null || obj.__Instance == IntPtr.Zero ||
                    fpdf_edit.FPDFPageObjGetType(obj) != PdfPageLayerHelper.FpdfPageObjImage)
                {
                    return false;
                }

                if (fpdf_edit.FPDFPageRemoveObject(page, obj) == 0)
                {
                    return false;
                }

                fpdf_edit.FPDFPageObjDestroy(obj);
                return fpdf_edit.FPDFPageGenerateContent(page) != 0;
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        });
    }

    public static (int PixelWidth, int PixelHeight) ProbeImageDimensions(byte[] imageBytes)
    {
        using var codec = SKCodec.Create(new MemoryStream(imageBytes));
        return codec is not null ? (codec.Info.Width, codec.Info.Height) : (200, 150);
    }

    private static SKBitmap ConvertToBgra8888(SKBitmap source)
    {
        if (source.ColorType == SKColorType.Bgra8888 && source.AlphaType == SKAlphaType.Premul)
        {
            return source.Copy();
        }

        var target = new SKBitmap(new SKImageInfo(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(target);
        canvas.DrawBitmap(source, 0, 0);
        return target;
    }
}
