using System.Runtime.InteropServices;
using System.Text;
using PDFiumCore;
using PdfStudio.Core.Documents;
using PdfStudio.Core.Layout;
using PdfStudio.Core.Pdfium;

namespace PdfStudio.Core.Editing;

internal static class PdfPageLayerHelper
{
    public const int FpdfPageObjText = 1;
    public const int FpdfPageObjPath = 2;
    public const int FpdfPageObjImage = 3;
    public const int FpdfPageObjForm = 5;
    public const int FpdfFontTrueType = 2;
    public const int FpdfFillModeWinding = 2;

    public const string MarkBasePage = "PdfStudioBasePage";
    public const string MarkMask = "PdfStudioMask";
    public const string MarkEdit = "PdfStudioEdit";
    public const string MarkRedact = "PdfStudioRedact";
    public const string MarkImage = "PdfStudioImage";

    public static void EnsurePageWrapped(
        PdfDocumentSession session,
        int pageIndex,
        double pageWidth,
        double pageHeight)
    {
        bool alreadyWrapped = PdfiumEngine.Instance.RunLocked(() => IsPageWrappedLocked(session.Handle, pageIndex));
        if (alreadyWrapped)
        {
            return;
        }

        byte[] snapshotBytes = session.SaveToBytes();
        PdfiumEngine.Instance.RunLocked(() =>
        {
            var doc = session.Handle;
            if (!IsPageWrappedLocked(doc, pageIndex))
            {
                WrapPageAsBaseXObjectLocked(doc, pageIndex, pageWidth, pageHeight, snapshotBytes);
            }
        });
    }

    public static bool IsPageWrappedLocked(FpdfDocumentT doc, int pageIndex)
    {
        var page = fpdfview.FPDF_LoadPage(doc, pageIndex);
        if (page is null || page.__Instance == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            int count = fpdf_edit.FPDFPageCountObjects(page);
            if (count <= 0)
            {
                return false;
            }

            var firstObj = fpdf_edit.FPDFPageGetObject(page, 0);
            return firstObj is not null
                && firstObj.__Instance != IntPtr.Zero
                && fpdf_edit.FPDFPageObjGetType(firstObj) == FpdfPageObjForm
                && HasMark(firstObj, MarkBasePage);
        }
        finally
        {
            fpdfview.FPDF_ClosePage(page);
        }
    }

    public static void WrapPageAsBaseXObjectLocked(
        FpdfDocumentT doc,
        int pageIndex,
        double pageWidth,
        double pageHeight,
        byte[] sourceBytes)
    {
        IntPtr srcPtr = Marshal.AllocHGlobal(sourceBytes.Length);
        try
        {
            Marshal.Copy(sourceBytes, 0, srcPtr, sourceBytes.Length);
            var srcDoc = fpdfview.FPDF_LoadMemDocument(srcPtr, sourceBytes.Length, null);
            if (srcDoc is null || srcDoc.__Instance == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to snapshot source PDF page.");
            }

            try
            {
                var newPage = fpdf_edit.FPDFPageNew(doc, pageIndex, pageWidth, pageHeight);
                if (newPage is null || newPage.__Instance == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Failed to create wrapper page.");
                }

                try
                {
                    var xobj = fpdf_ppo.FPDF_NewXObjectFromPage(doc, srcDoc, pageIndex);
                    if (xobj is null || xobj.__Instance == IntPtr.Zero)
                    {
                        throw new InvalidOperationException("Failed to import page as Form XObject.");
                    }

                    try
                    {
                        var formObj = fpdf_ppo.FPDF_NewFormObjectFromXObject(xobj);
                        if (formObj is not null && formObj.__Instance != IntPtr.Zero)
                        {
                            fpdf_edit.FPDFPageObjAddMark(formObj, MarkBasePage);
                            fpdf_edit.FPDFPageInsertObject(newPage, formObj);
                        }
                    }
                    finally
                    {
                        fpdf_ppo.FPDF_CloseXObject(xobj);
                    }

                    fpdf_edit.FPDFPageGenerateContent(newPage);
                }
                finally
                {
                    fpdfview.FPDF_ClosePage(newPage);
                }

                fpdf_edit.FPDFPageDelete(doc, pageIndex + 1);
            }
            finally
            {
                fpdfview.FPDF_CloseDocument(srcDoc);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(srcPtr);
        }
    }

    public static bool HasMark(FpdfPageobjectT obj, string expectedName)
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

    public static PdfColor SamplePageBackgroundColorLocked(
        FpdfPageT page,
        double pageWidth,
        double pageHeight,
        PdfRect bounds)
    {
        int bmpW = Math.Clamp((int)Math.Ceiling(pageWidth), 32, 2048);
        int bmpH = Math.Clamp((int)Math.Ceiling(pageHeight), 32, 2048);
        double scaleX = bmpW / Math.Max(1.0, pageWidth);
        double scaleY = bmpH / Math.Max(1.0, pageHeight);

        var bmp = fpdfview.FPDFBitmapCreate(bmpW, bmpH, 0);
        if (bmp is null || bmp.__Instance == IntPtr.Zero)
        {
            return new PdfColor(255, 255, 255, 255);
        }

        try
        {
            fpdfview.FPDFBitmapFillRect(bmp, 0, 0, bmpW, bmpH, 0xFFFFFFFF);
            fpdfview.FPDF_RenderPageBitmap(bmp, page, 0, 0, bmpW, bmpH, 0, 0);

            IntPtr buf = fpdfview.FPDFBitmapGetBuffer(bmp);
            int stride = fpdfview.FPDFBitmapGetStride(bmp);
            if (buf == IntPtr.Zero || stride <= 0)
            {
                return new PdfColor(255, 255, 255, 255);
            }

            var samples = new List<(byte R, byte G, byte B)>();
            void SampleAt(double ptX, double ptY)
            {
                int px = Math.Clamp((int)Math.Round(ptX * scaleX), 0, bmpW - 1);
                int py = Math.Clamp((int)Math.Round(ptY * scaleY), 0, bmpH - 1);
                int offset = (py * stride) + (px * 4);
                byte b = Marshal.ReadByte(buf, offset + 0);
                byte g = Marshal.ReadByte(buf, offset + 1);
                byte r = Marshal.ReadByte(buf, offset + 2);
                samples.Add((r, g, b));
            }

            for (int step = 0; step <= 4; step++)
            {
                double t = step / 4.0;
                double x = bounds.Left + (bounds.Width * t);
                double y = bounds.Top + (bounds.Height * t);
                SampleAt(x, bounds.Top - 1.5);
                SampleAt(x, bounds.Bottom + 1.5);
                SampleAt(bounds.Left - 2.0, y);
                SampleAt(bounds.Right + 2.0, y);
            }

            var best = samples
                .GroupBy(c => (R: (byte)(c.R & 0xFC), G: (byte)(c.G & 0xFC), B: (byte)(c.B & 0xFC)))
                .OrderByDescending(g => g.Count())
                .ThenByDescending(g => g.Key.R + g.Key.G + g.Key.B)
                .First()
                .First();

            return new PdfColor(best.R, best.G, best.B, 255);
        }
        finally
        {
            fpdfview.FPDFBitmapDestroy(bmp);
        }
    }

    public static byte[] SaveDocBytesInternal(FpdfDocumentT doc)
    {
        using var ms = new MemoryStream();
        using var fileWrite = new FPDF_FILEWRITE_
        {
            Version = 1,
            WriteBlock = (_, pData, size) =>
            {
                if (size == 0) return 1;
                int count = checked((int)size);
                byte[] buf = new byte[count];
                Marshal.Copy(pData, buf, 0, count);
                ms.Write(buf, 0, count);
                return 1;
            }
        };

        fpdf_save.FPDF_SaveAsCopy(doc, fileWrite, 0);
        return ms.ToArray();
    }
}
