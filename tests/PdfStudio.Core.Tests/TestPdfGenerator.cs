using System.Globalization;
using System.Text;

namespace PdfStudio.Core.Tests;

internal static class TestPdfGenerator
{
    /// <summary>
    /// Generates a valid two-page PDF 1.7 byte array with dark text and graphics so rendered bitmaps are non-blank.
    /// </summary>
    public static byte[] CreateSampleTwoPagePdf()
    {
        var objects = new List<string>
        {
            // 1: Catalog
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            // 2: Pages
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>\nendobj\n",
            // 3: Page 1 (US Letter: 612 x 792 pts)
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 6 0 R >>\nendobj\n",
            // 4: Page 2 (A4: 595 x 842 pts)
            "4 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 5 0 R >> >> /Contents 7 0 R >>\nendobj\n",
            // 5: Font
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
            // 6: Page 1 Content
            BuildContentStreamObject(6, "BT /F1 24 Tf 72 700 Td (PdfStudio Milestone 1) Tj ET\n0.2 0.4 0.8 rg 72 620 200 40 re f\n"),
            // 7: Page 2 Content
            BuildContentStreamObject(7, "BT /F1 18 Tf 72 740 Td (Page Two Summary) Tj ET\n")
        };

        using var ms = new MemoryStream();
        void WriteAscii(string text)
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            ms.Write(bytes, 0, bytes.Length);
        }

        WriteAscii("%PDF-1.7\n");
        var offsets = new List<long> { 0 };

        foreach (var obj in objects)
        {
            offsets.Add(ms.Position);
            WriteAscii(obj);
        }

        long xrefOffset = ms.Position;
        WriteAscii($"xref\n0 {objects.Count + 1}\n");
        WriteAscii("0000000000 65535 f \n");
        for (int i = 1; i < offsets.Count; i++)
        {
            WriteAscii(string.Format(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offsets[i]));
        }

        WriteAscii($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        return ms.ToArray();
    }

    private static string BuildContentStreamObject(int objNumber, string streamContent)
    {
        var byteLen = Encoding.ASCII.GetByteCount(streamContent);
        return $"{objNumber} 0 obj\n<< /Length {byteLen} >>\nstream\n{streamContent}endstream\nendobj\n";
    }

    public static byte[] CreateSinglePagePdfFromStream(string contentStream, double widthPts = 612, double heightPts = 792)
    {
        var objects = new List<string>
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            string.Format(
                CultureInfo.InvariantCulture,
                "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {0} {1}] /Resources << /Font << /F1 4 0 R /F2 5 0 R /F3 6 0 R >> >> /Contents 7 0 R >>\nendobj\n",
                widthPts,
                heightPts),
            "4 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>\nendobj\n",
            "6 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Times-Italic >>\nendobj\n",
            BuildContentStreamObject(7, contentStream)
        };

        using var ms = new MemoryStream();
        void WriteAscii(string text)
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            ms.Write(bytes, 0, bytes.Length);
        }

        WriteAscii("%PDF-1.7\n");
        var offsets = new List<long> { 0 };
        foreach (var obj in objects)
        {
            offsets.Add(ms.Position);
            WriteAscii(obj);
        }

        long xrefOffset = ms.Position;
        WriteAscii($"xref\n0 {objects.Count + 1}\n");
        WriteAscii("0000000000 65535 f \n");
        for (int i = 1; i < offsets.Count; i++)
        {
            WriteAscii(string.Format(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offsets[i]));
        }

        WriteAscii($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        return ms.ToArray();
    }
}
