using System.Runtime.InteropServices;
using PDFiumCore;
using PdfStudio.Core.Documents;
using PdfStudio.Core.Pdfium;

namespace PdfStudio.Core.Editing;

/// <summary>
/// Organizes pages inside a <see cref="PdfDocumentSession"/>: insert blank pages, delete, reorder, and merge external PDFs.
/// </summary>
public sealed class PdfPageOrganizer
{
    public void InsertBlankPage(PdfDocumentSession session, int insertAtIndex, double widthPts = 612, double heightPts = 792)
    {
        ArgumentNullException.ThrowIfNull(session);
        int clamped = Math.Clamp(insertAtIndex, 0, session.Pages.Count);

        PdfiumEngine.Instance.RunLocked(() =>
        {
            var page = fpdf_edit.FPDFPageNew(session.Handle, clamped, widthPts, heightPts);
            if (page is null || page.__Instance == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to create blank PDF page.");
            }

            fpdfview.FPDF_ClosePage(page);
        });

        session.RefreshPagesAndMetadata();
    }

    public void DeletePage(PdfDocumentSession session, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Pages.Count <= 1)
        {
            throw new InvalidOperationException("Cannot delete the only remaining page in the document.");
        }

        if (pageIndex < 0 || pageIndex >= session.Pages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        PdfiumEngine.Instance.RunLocked(() =>
        {
            fpdf_edit.FPDFPageDelete(session.Handle, pageIndex);
        });

        session.RefreshPagesAndMetadata();
    }

    public int MergePdf(PdfDocumentSession targetSession, byte[] sourcePdfBytes, int insertAtIndex)
    {
        ArgumentNullException.ThrowIfNull(targetSession);
        ArgumentNullException.ThrowIfNull(sourcePdfBytes);
        if (sourcePdfBytes.Length == 0)
        {
            throw new ArgumentException("Source PDF cannot be empty.", nameof(sourcePdfBytes));
        }

        int clampedInsert = Math.Clamp(insertAtIndex, 0, targetSession.Pages.Count);
        int importedCount = 0;

        IntPtr srcPtr = Marshal.AllocHGlobal(sourcePdfBytes.Length);
        try
        {
            Marshal.Copy(sourcePdfBytes, 0, srcPtr, sourcePdfBytes.Length);
            PdfiumEngine.Instance.RunLocked(() =>
            {
                var srcDoc = fpdfview.FPDF_LoadMemDocument(srcPtr, sourcePdfBytes.Length, null);
                if (srcDoc is null || srcDoc.__Instance == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Could not open PDF to merge.");
                }

                try
                {
                    importedCount = fpdfview.FPDF_GetPageCount(srcDoc);
                    if (importedCount <= 0)
                    {
                        return;
                    }

                    if (fpdf_ppo.FPDF_ImportPages(targetSession.Handle, srcDoc, null, clampedInsert) == 0)
                    {
                        throw new InvalidOperationException("Failed to merge pages from source PDF.");
                    }
                }
                finally
                {
                    fpdfview.FPDF_CloseDocument(srcDoc);
                }
            });
        }
        finally
        {
            Marshal.FreeHGlobal(srcPtr);
        }

        targetSession.RefreshPagesAndMetadata();
        return importedCount;
    }

    /// <summary>
    /// Reorders a page from <paramref name="fromIndex"/> to <paramref name="toIndex"/> and returns a rebuilt
    /// <see cref="PdfDocumentSession"/> preserving the original file path.
    /// </summary>
    public PdfDocumentSession ReorderPage(PdfDocumentSession session, int fromIndex, int toIndex)
    {
        ArgumentNullException.ThrowIfNull(session);
        int count = session.Pages.Count;
        if (fromIndex < 0 || fromIndex >= count) throw new ArgumentOutOfRangeException(nameof(fromIndex));
        if (toIndex < 0 || toIndex >= count) throw new ArgumentOutOfRangeException(nameof(toIndex));

        if (fromIndex == toIndex)
        {
            return session;
        }

        var order = Enumerable.Range(0, count).ToList();
        int moved = order[fromIndex];
        order.RemoveAt(fromIndex);
        order.Insert(toIndex, moved);
        int[] indices = order.ToArray();

        byte[] sourceBytes = session.SaveToBytes();
        byte[] reorderedBytes;

        IntPtr srcPtr = Marshal.AllocHGlobal(sourceBytes.Length);
        try
        {
            Marshal.Copy(sourceBytes, 0, srcPtr, sourceBytes.Length);
            reorderedBytes = PdfiumEngine.Instance.RunLocked(() =>
            {
                var srcDoc = fpdfview.FPDF_LoadMemDocument(srcPtr, sourceBytes.Length, null);
                if (srcDoc is null || srcDoc.__Instance == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Failed to snapshot PDF for page reorder.");
                }

                var newDoc = fpdf_edit.FPDF_CreateNewDocument();
                try
                {
                    if (fpdf_ppo.FPDF_ImportPagesByIndex(newDoc, srcDoc, ref indices[0], (uint)indices.Length, 0) == 0)
                    {
                        throw new InvalidOperationException("Failed to reorder PDF pages.");
                    }

                    return PdfPageLayerHelper.SaveDocBytesInternal(newDoc);
                }
                finally
                {
                    fpdfview.FPDF_CloseDocument(newDoc);
                    fpdfview.FPDF_CloseDocument(srcDoc);
                }
            });
        }
        finally
        {
            Marshal.FreeHGlobal(srcPtr);
        }

        return PdfDocumentSession.OpenBytesWithFilePath(
            reorderedBytes,
            session.Metadata.FilePath,
            session.Metadata.FileName);
    }
}
