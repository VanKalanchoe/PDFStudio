using PDFiumCore;
using PdfStudio.Core.Documents;
using PdfStudio.Core.Editing;
using PdfStudio.Core.Layout;
using PdfStudio.Core.Pdfium;
using SkiaSharp;
using Tesseract;

namespace PdfStudio.Core.Ocr;

public sealed record PdfOcrLineResult(
    string Text,
    PdfRect Bounds,
    float Confidence);

public sealed record PdfOcrPageResult(
    int PageIndex,
    string FullText,
    IReadOnlyList<PdfOcrLineResult> Lines,
    double MeanConfidence,
    bool EmbeddedSearchableText);

/// <summary>
/// Runs Tesseract OCR on scanned or rasterized PDF pages and optionally embeds an invisible
/// searchable PDF text layer matching recognized line positions.
/// </summary>
public sealed class PdfOcrService
{
    private const double OcrDpi = 300.0;
    private readonly PdfPageRenderer _renderer;
    private readonly FontFallbackResolver _fontResolver;

    public PdfOcrService(PdfPageRenderer? renderer = null, FontFallbackResolver? fontResolver = null)
    {
        _renderer = renderer ?? new PdfPageRenderer();
        _fontResolver = fontResolver ?? new FontFallbackResolver();
    }

    public async Task<PdfOcrPageResult> RunOcrOnPageAsync(
        PdfDocumentSession session,
        int pageIndex,
        string language = "eng",
        bool embedInvisibleTextLayer = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (pageIndex < 0 || pageIndex >= session.Pages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        string tessdataDir = ResolveTessdataDirectory(language);
        var pageInfo = session.Pages[pageIndex];
        double scale = OcrDpi / 72.0;
        int pixelW = Math.Clamp((int)Math.Round(pageInfo.WidthPoints * scale), 64, 6000);
        int pixelH = Math.Clamp((int)Math.Round(pageInfo.HeightPoints * scale), 64, 6000);

        using var skBitmap = await _renderer.RenderPageAsync(
            session,
            pageIndex,
            pixelW,
            pixelH,
            new PdfRenderOptions(RenderAnnotations: false, OptimizeTextForLcd: false),
            cancellationToken).ConfigureAwait(false);

        using var image = SKImage.FromBitmap(skBitmap);
        using var pngData = image.Encode(SKEncodedImageFormat.Png, 100);
        byte[] pngBytes = pngData.ToArray();

        var (fullText, lines, meanConfidence) = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var engine = new TesseractEngine(tessdataDir, language, EngineMode.Default);
            using var pix = Pix.LoadFromMemory(pngBytes);
            using var ocrPage = engine.Process(pix);

            var recognizedLines = new List<PdfOcrLineResult>();
            using var iter = ocrPage.GetIterator();
            iter.Begin();
            do
            {
                if (iter.TryGetBoundingBox(PageIteratorLevel.TextLine, out var bbox))
                {
                    string? lineText = iter.GetText(PageIteratorLevel.TextLine)?.Trim();
                    if (!string.IsNullOrWhiteSpace(lineText))
                    {
                        float conf = iter.GetConfidence(PageIteratorLevel.TextLine);
                        double left = bbox.X1 / scale;
                        double top = bbox.Y1 / scale;
                        double right = bbox.X2 / scale;
                        double bottom = bbox.Y2 / scale;
                        recognizedLines.Add(new PdfOcrLineResult(lineText, new PdfRect(left, top, right, bottom), conf));
                    }
                }
            }
            while (iter.Next(PageIteratorLevel.TextLine));

            return (ocrPage.GetText().Trim(), recognizedLines, ocrPage.GetMeanConfidence() * 100.0);
        }, cancellationToken).ConfigureAwait(false);

        bool embedded = false;
        if (embedInvisibleTextLayer && lines.Count > 0)
        {
            embedded = EmbedInvisibleTextLayer(session, pageIndex, pageInfo.WidthPoints, pageInfo.HeightPoints, lines);
        }

        return new PdfOcrPageResult(pageIndex, fullText, lines, Math.Round(meanConfidence, 1), embedded);
    }

    private bool EmbedInvisibleTextLayer(
        PdfDocumentSession session,
        int pageIndex,
        double pageWidth,
        double pageHeight,
        List<PdfOcrLineResult> lines)
    {
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
                var font = fpdf_edit.FPDFTextLoadStandardFont(doc, "Helvetica");
                FpdfFontT? unicodeFallbackFont = null;

                try
                {
                    foreach (var line in lines)
                    {
                        double fontSize = Math.Clamp(line.Bounds.Height * 0.85, 6.0, 72.0);
                        bool isAscii = line.Text.All(c => c <= 0x7E);
                        FpdfFontT? activeFont = font;

                        if (!isAscii)
                        {
                            if (unicodeFallbackFont is null)
                            {
                                var coverage = _fontResolver.ResolveCoverage("Arial", false, false, line.Text);
                                if (coverage.PrimaryFont.FontBytes.Length > 0)
                                {
                                    unsafe
                                    {
                                        fixed (byte* ptr = coverage.PrimaryFont.FontBytes)
                                        {
                                            unicodeFallbackFont = fpdf_edit.FPDFTextLoadFont(
                                                doc,
                                                ptr,
                                                (uint)coverage.PrimaryFont.FontBytes.Length,
                                                PdfPageLayerHelper.FpdfFontTrueType,
                                                1);
                                        }
                                    }
                                }
                            }

                            if (unicodeFallbackFont is not null && unicodeFallbackFont.__Instance != IntPtr.Zero)
                            {
                                activeFont = unicodeFallbackFont;
                            }
                        }

                        if (activeFont is null || activeFont.__Instance == IntPtr.Zero)
                        {
                            continue;
                        }

                        var textObj = fpdf_edit.FPDFPageObjCreateTextObj(doc, activeFont, (float)fontSize);
                        if (textObj is null || textObj.__Instance == IntPtr.Zero)
                        {
                            continue;
                        }

                        var utf16 = (line.Text + "\0").ToCharArray();
                        ushort[] units = new ushort[utf16.Length];
                        for (int k = 0; k < utf16.Length; k++)
                        {
                            units[k] = utf16[k];
                        }

                        if (fpdf_edit.FPDFTextSetText(textObj, ref units[0]) == 0)
                        {
                            fpdf_edit.FPDFPageObjDestroy(textObj);
                            continue;
                        }

                        fpdf_edit.FPDFTextObjSetTextRenderMode(textObj, FPDF_TEXT_RENDERMODE.FPDF_TEXTRENDERMODE_INVISIBLE);

                        double baselineTopDown = line.Bounds.Bottom - (fontSize * 0.15);
                        double pdfY = pageHeight - baselineTopDown;
                        using var matrix = new FS_MATRIX_
                        {
                            A = 1,
                            D = 1,
                            E = (float)line.Bounds.Left,
                            F = (float)pdfY
                        };

                        fpdf_edit.FPDFPageObjSetMatrix(textObj, matrix);
                        fpdf_edit.FPDFPageObjAddMark(textObj, PdfPageLayerHelper.MarkEdit);
                        fpdf_edit.FPDFPageInsertObject(page, textObj);
                    }

                    return fpdf_edit.FPDFPageGenerateContent(page) != 0;
                }
                finally
                {
                    if (font is not null && font.__Instance != IntPtr.Zero)
                    {
                        fpdf_edit.FPDFFontClose(font);
                    }

                    if (unicodeFallbackFont is not null && unicodeFallbackFont.__Instance != IntPtr.Zero)
                    {
                        fpdf_edit.FPDFFontClose(unicodeFallbackFont);
                    }
                }
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        });
    }

    public static string ResolveTessdataDirectory(string language = "eng")
    {
        string fileName = $"{language}.traineddata";
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "tessdata"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PdfStudio", "tessdata"),
            Environment.GetEnvironmentVariable("TESSDATA_PREFIX") ?? string.Empty,
            @"C:\Program Files\Tesseract-OCR\tessdata"
        ];

        foreach (string dir in candidates)
        {
            if (!string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, fileName)))
            {
                return dir;
            }
        }

        throw new FileNotFoundException(
            $"Tesseract traineddata '{fileName}' was not found. Checked: {string.Join(", ", candidates.Where(c => !string.IsNullOrWhiteSpace(c)))}");
    }
}
