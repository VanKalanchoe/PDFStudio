using System.Text;
using PDFiumCore;
using PdfStudio.Core.Documents;
using PdfStudio.Core.Layout;
using PdfStudio.Core.Pdfium;

namespace PdfStudio.Core.Editing;

public enum PdfAnnotationKind
{
    Highlight,
    Comment
}

public sealed record PdfAnnotationInfo(
    int Index,
    PdfAnnotationKind Kind,
    PdfRect Bounds,
    string Contents,
    string Author,
    PdfColor Color);

/// <summary>
/// Creates, reads, updates, and deletes PDF annotations (highlights &amp; sticky-note comments) via PDFium.
/// </summary>
public sealed class PdfAnnotationService
{
    private const int FpdfAnnotText = 1;
    private const int FpdfAnnotHighlight = 9;

    public IReadOnlyList<PdfAnnotationInfo> GetAnnotations(PdfDocumentSession session, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (pageIndex < 0 || pageIndex >= session.Pages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        double pageHeight = session.Pages[pageIndex].HeightPoints;
        return PdfiumEngine.Instance.RunLocked(() =>
        {
            var page = fpdfview.FPDF_LoadPage(session.Handle, pageIndex);
            if (page is null || page.__Instance == IntPtr.Zero)
            {
                return (IReadOnlyList<PdfAnnotationInfo>)[];
            }

            try
            {
                int count = fpdf_annot.FPDFPageGetAnnotCount(page);
                var result = new List<PdfAnnotationInfo>(Math.Max(0, count));

                for (int i = 0; i < count; i++)
                {
                    var annot = fpdf_annot.FPDFPageGetAnnot(page, i);
                    if (annot is null || annot.__Instance == IntPtr.Zero)
                    {
                        continue;
                    }

                    try
                    {
                        int subtype = fpdf_annot.FPDFAnnotGetSubtype(annot);
                        if (subtype != FpdfAnnotHighlight && subtype != FpdfAnnotText)
                        {
                            continue;
                        }

                        using var rect = new FS_RECTF_();
                        if (fpdf_annot.FPDFAnnotGetRect(annot, rect) == 0)
                        {
                            continue;
                        }

                        var topDownBounds = new PdfRect(
                            Math.Min(rect.Left, rect.Right),
                            Math.Min(pageHeight - rect.Top, pageHeight - rect.Bottom),
                            Math.Max(rect.Left, rect.Right),
                            Math.Max(pageHeight - rect.Top, pageHeight - rect.Bottom));

                        uint r = 245, g = 209, b = 66, a = 128;
                        _ = fpdf_annot.FPDFAnnotGetColor(annot, FPDFANNOT_COLORTYPE.FPDFANNOT_COLORTYPE_Color, ref r, ref g, ref b, ref a);

                        string contents = ReadAnnotString(annot, "Contents");
                        string author = ReadAnnotString(annot, "T");

                        result.Add(new PdfAnnotationInfo(
                            Index: i,
                            Kind: subtype == FpdfAnnotHighlight ? PdfAnnotationKind.Highlight : PdfAnnotationKind.Comment,
                            Bounds: topDownBounds,
                            Contents: contents,
                            Author: author,
                            Color: new PdfColor((byte)Math.Min(r, 255), (byte)Math.Min(g, 255), (byte)Math.Min(b, 255), (byte)Math.Min(a, 255))));
                    }
                    finally
                    {
                        fpdf_annot.FPDFPageCloseAnnot(annot);
                    }
                }

                return result;
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        });
    }

    public PdfAnnotationInfo AddHighlight(
        PdfDocumentSession session,
        int pageIndex,
        PdfRect bounds,
        PdfColor? color = null,
        string? comment = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (pageIndex < 0 || pageIndex >= session.Pages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        double pageHeight = session.Pages[pageIndex].HeightPoints;
        var hlColor = color ?? new PdfColor(245, 209, 66, 115);

        return PdfiumEngine.Instance.RunLocked(() =>
        {
            var page = fpdfview.FPDF_LoadPage(session.Handle, pageIndex);
            if (page is null || page.__Instance == IntPtr.Zero)
            {
                throw new InvalidOperationException($"Failed to load page {pageIndex + 1}.");
            }

            try
            {
                var annot = fpdf_annot.FPDFPageCreateAnnot(page, FpdfAnnotHighlight);
                if (annot is null || annot.__Instance == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Failed to create PDF highlight annotation.");
                }

                try
                {
                    float left = (float)bounds.Left;
                    float right = (float)bounds.Right;
                    float top = (float)(pageHeight - bounds.Top);
                    float bottom = (float)(pageHeight - bounds.Bottom);

                    fpdf_annot.FPDFAnnotSetColor(
                        annot,
                        FPDFANNOT_COLORTYPE.FPDFANNOT_COLORTYPE_Color,
                        hlColor.R,
                        hlColor.G,
                        hlColor.B,
                        hlColor.A);

                    using var fsRect = new FS_RECTF_
                    {
                        Left = left,
                        Top = top,
                        Right = right,
                        Bottom = bottom
                    };
                    fpdf_annot.FPDFAnnotSetRect(annot, fsRect);

                    using var quads = new FS_QUADPOINTSF
                    {
                        X1 = left,
                        Y1 = top,
                        X2 = right,
                        Y2 = top,
                        X3 = left,
                        Y3 = bottom,
                        X4 = right,
                        Y4 = bottom
                    };
                    fpdf_annot.FPDFAnnotAppendAttachmentPoints(annot, quads);

                    if (!string.IsNullOrWhiteSpace(comment))
                    {
                        SetAnnotString(annot, "Contents", comment);
                    }

                    int idx = fpdf_annot.FPDFPageGetAnnotIndex(page, annot);
                    return new PdfAnnotationInfo(idx, PdfAnnotationKind.Highlight, bounds, comment ?? string.Empty, "PdfStudio", hlColor);
                }
                finally
                {
                    fpdf_annot.FPDFPageCloseAnnot(annot);
                }
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        });
    }

    public PdfAnnotationInfo AddComment(
        PdfDocumentSession session,
        int pageIndex,
        double ptX,
        double ptY,
        string commentText,
        string author = "PdfStudio")
    {
        ArgumentNullException.ThrowIfNull(session);
        if (pageIndex < 0 || pageIndex >= session.Pages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        double pageHeight = session.Pages[pageIndex].HeightPoints;
        var noteColor = new PdfColor(217, 157, 57, 255);
        var bounds = new PdfRect(ptX, ptY, ptX + 20.0, ptY + 20.0);

        return PdfiumEngine.Instance.RunLocked(() =>
        {
            var page = fpdfview.FPDF_LoadPage(session.Handle, pageIndex);
            if (page is null || page.__Instance == IntPtr.Zero)
            {
                throw new InvalidOperationException($"Failed to load page {pageIndex + 1}.");
            }

            try
            {
                var annot = fpdf_annot.FPDFPageCreateAnnot(page, FpdfAnnotText);
                if (annot is null || annot.__Instance == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Failed to create PDF comment annotation.");
                }

                try
                {
                    fpdf_annot.FPDFAnnotSetColor(
                        annot,
                        FPDFANNOT_COLORTYPE.FPDFANNOT_COLORTYPE_Color,
                        noteColor.R,
                        noteColor.G,
                        noteColor.B,
                        noteColor.A);

                    using var fsRect = new FS_RECTF_
                    {
                        Left = (float)bounds.Left,
                        Top = (float)(pageHeight - bounds.Top),
                        Right = (float)bounds.Right,
                        Bottom = (float)(pageHeight - bounds.Bottom)
                    };
                    fpdf_annot.FPDFAnnotSetRect(annot, fsRect);

                    SetAnnotString(annot, "Contents", commentText ?? string.Empty);
                    SetAnnotString(annot, "T", author);

                    int idx = fpdf_annot.FPDFPageGetAnnotIndex(page, annot);
                    return new PdfAnnotationInfo(idx, PdfAnnotationKind.Comment, bounds, commentText ?? string.Empty, author, noteColor);
                }
                finally
                {
                    fpdf_annot.FPDFPageCloseAnnot(annot);
                }
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        });
    }

    public bool DeleteAnnotation(PdfDocumentSession session, int pageIndex, int annotIndex)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (pageIndex < 0 || pageIndex >= session.Pages.Count)
        {
            return false;
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
                return fpdf_annot.FPDFPageRemoveAnnot(page, annotIndex) != 0;
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        });
    }

    private static string ReadAnnotString(FpdfAnnotationT annot, string key)
    {
        ushort dummy = 0;
        uint len = fpdf_annot.FPDFAnnotGetStringValue(annot, key, ref dummy, 0);
        if (len <= 2)
        {
            return string.Empty;
        }

        ushort[] buf = new ushort[(len + 1) / 2];
        uint written = fpdf_annot.FPDFAnnotGetStringValue(annot, key, ref buf[0], len);
        if (written <= 2)
        {
            return string.Empty;
        }

        byte[] bytes = new byte[written];
        Buffer.BlockCopy(buf, 0, bytes, 0, (int)written);
        return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
    }

    private static void SetAnnotString(FpdfAnnotationT annot, string key, string value)
    {
        var chars = (value + "\0").ToCharArray();
        ushort[] buf = new ushort[chars.Length];
        for (int i = 0; i < chars.Length; i++)
        {
            buf[i] = chars[i];
        }

        fpdf_annot.FPDFAnnotSetStringValue(annot, key, ref buf[0]);
    }
}
