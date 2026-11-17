using System.Globalization;
using System.Text;

namespace PdfStudio.App.Services;

internal static class SamplePdfBuilder
{
    public static byte[] CreateBusinessReportSample()
    {
        var objects = new List<string>
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 6 0 R /F2 7 0 R >> >> /Contents 8 0 R >>\nendobj\n",
            "4 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 6 0 R /F2 7 0 R >> >> /Contents 9 0 R >>\nendobj\n",
            "5 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 6 0 R /F2 7 0 R >> >> /Contents 10 0 R >>\nendobj\n",
            "6 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
            "7 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>\nendobj\n",
            BuildStream(8, BuildPage1Content()),
            BuildStream(9, BuildPage2Content()),
            BuildStream(10, BuildPage3Content())
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

    private static string BuildStream(int id, string content)
    {
        int len = Encoding.ASCII.GetByteCount(content);
        return $"{id} 0 obj\n<< /Length {len} >>\nstream\n{content}endstream\nendobj\n";
    }

    private static string BuildPage1Content() =>
        """
        0.18 0.22 0.31 rg 0 580 612 212 re f
        0.26 0.53 0.96 rg 72 622 48 4 re f
        1 1 1 rg
        BT /F2 28 Tf 72 680 Td (Annual Report 2026) Tj ET
        0.78 0.82 0.90 rg
        BT /F1 13 Tf 72 648 Td (Executive Strategy & Financial Outlook) Tj ET
        0.12 0.14 0.19 rg
        BT /F2 15 Tf 72 520 Td (Prepared with PdfStudio Desktop Engine) Tj ET
        BT /F1 11 Tf 72 492 Td (Milestone 1: High-DPI PDFium + SkiaSharp Viewer & Slate-Navy UI Shell.) Tj ET
        0.85 0.87 0.92 RG 1 w 72 80 m 540 80 l S
        0.45 0.49 0.57 rg
        BT /F1 9 Tf 72 56 Td (Confidential Business Report) Tj 445 0 Td (1) Tj ET
        """;

    private static string BuildPage2Content() =>
        """
        0.40 0.44 0.52 rg
        BT /F1 9 Tf 72 742 Td (Business Report) Tj ET
        0.85 0.87 0.92 RG 0.75 w 72 728 m 540 728 l S
        0.09 0.11 0.16 rg
        BT /F2 22 Tf 72 686 Td (Q3 Financial Summary) Tj ET
        BT /F1 11 Tf 72 644 Td (Executive Overview: Enterprise revenue accelerated across cloud licensing and) Tj ET
        BT /F1 11 Tf 72 628 Td (subscription renewals during the fiscal quarter, supported by higher client) Tj ET
        BT /F1 11 Tf 72 612 Td (retention and disciplined operating margins.) Tj ET
        BT /F1 11 Tf 72 578 Td (Market performance remained resilient across core segments, reflecting a) Tj ET
        BT /F1 11 Tf 72 562 Td (projected growth of 18.4% in Q3 as enterprise adoption expands ahead of) Tj ET
        BT /F1 11 Tf 72 546 Td (annual targets.) Tj ET
        BT /F1 11 Tf 72 512 Td (Capital allocation remained focused on platform reliability and R&D delivery,) Tj ET
        BT /F1 11 Tf 72 496 Td (supporting steady gains in operating cash flow.) Tj ET
        0.85 0.87 0.92 RG 1 w 72 190 m 540 190 l S
        0.26 0.53 0.96 rg
        104 190 22 96 re f 164 190 22 126 re f 224 190 22 152 re f 284 190 22 140 re f 344 190 22 178 re f 404 190 22 162 re f 464 190 22 194 re f
        0.16 0.21 0.33 rg
        128 190 22 114 re f 188 190 22 104 re f 248 190 22 134 re f 308 190 22 158 re f 368 190 22 146 re f 428 190 22 184 re f 488 190 22 212 re f
        0.45 0.49 0.57 rg
        BT /F1 9 Tf 72 56 Td (A4 business report) Tj 455 0 Td (2) Tj ET
        """;

    private static string BuildPage3Content() =>
        """
        0.40 0.44 0.52 rg
        BT /F1 9 Tf 72 742 Td (Business Report) Tj ET
        0.85 0.87 0.92 RG 0.75 w 72 728 m 540 728 l S
        0.09 0.11 0.16 rg
        BT /F2 20 Tf 72 686 Td (Q4 Operational Forecast) Tj ET
        BT /F1 11 Tf 72 644 Td (Regional infrastructure expansion continues across DACH and EU cloud clusters,) Tj ET
        BT /F1 11 Tf 72 628 Td (with automated document processing throughput up 31% year-over-year.) Tj ET
        0.94 0.95 0.98 rg 72 470 468 120 re f
        0.26 0.53 0.96 rg 72 586 468 4 re f
        0.12 0.14 0.19 rg
        BT /F2 12 Tf 92 554 Td (Key Metric Highlights) Tj ET
        BT /F1 11 Tf 92 526 Td (- Gross Margin: 78.2% (+210 bps YoY)) Tj ET
        BT /F1 11 Tf 92 504 Td (- Net Dollar Retention: 118.4%) Tj ET
        BT /F1 11 Tf 92 482 Td (- Free Cash Flow Conversion: 92.0%) Tj ET
        0.45 0.49 0.57 rg
        BT /F1 9 Tf 72 56 Td (A4 business report) Tj 455 0 Td (3) Tj ET
        """;
}
