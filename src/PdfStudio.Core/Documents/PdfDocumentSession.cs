using System.Runtime.InteropServices;
using System.Text;
using PDFiumCore;
using PdfStudio.Core.Pdfium;

namespace PdfStudio.Core.Documents;

/// <summary>
/// Represents an open PDF document backed by PDFium, exposing metadata, outlines, and thread-safe page access.
/// </summary>
public sealed class PdfDocumentSession : IDisposable
{
    private readonly FpdfDocumentT _document;
    private readonly IntPtr _pinnedMemoryPtr;
    private bool _disposed;

    internal PdfDocumentSession(
        FpdfDocumentT document,
        IntPtr pinnedMemoryPtr,
        PdfDocumentMetadata metadata,
        IReadOnlyList<PdfPageInfo> pages,
        IReadOnlyList<PdfOutlineNode> outlines)
    {
        _document = document;
        _pinnedMemoryPtr = pinnedMemoryPtr;
        Metadata = metadata;
        _pages = pages as List<PdfPageInfo> ?? pages.ToList();
        Outlines = outlines;
    }

    public PdfDocumentMetadata Metadata { get; private set; }
    public IReadOnlyList<PdfPageInfo> Pages => _pages;
    public IReadOnlyList<PdfOutlineNode> Outlines { get; }

    private readonly List<PdfPageInfo> _pages;

    internal FpdfDocumentT Handle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _document;
        }
    }

    public static PdfDocumentSession OpenFile(string filePath, string? password = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path must not be empty.", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("PDF file not found.", filePath);
        }

        byte[] fileBytes = File.ReadAllBytes(filePath);
        return OpenBytesInternal(fileBytes, filePath, Path.GetFileName(filePath), password);
    }

    public static PdfDocumentSession OpenBytes(byte[] pdfBytes, string displayName, string? password = null) =>
        OpenBytesInternal(pdfBytes, null, displayName, password);

    public static PdfDocumentSession OpenBytesWithFilePath(
        byte[] pdfBytes,
        string? filePath,
        string displayName,
        string? password = null) =>
        OpenBytesInternal(pdfBytes, filePath, displayName, password);

    private static PdfDocumentSession OpenBytesInternal(
        byte[] pdfBytes,
        string? filePath,
        string displayName,
        string? password)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);
        if (pdfBytes.Length == 0)
        {
            throw new ArgumentException("PDF byte buffer cannot be empty.", nameof(pdfBytes));
        }

        var nativeBuffer = Marshal.AllocHGlobal(pdfBytes.Length);
        try
        {
            Marshal.Copy(pdfBytes, 0, nativeBuffer, pdfBytes.Length);
            return PdfiumEngine.Instance.RunLocked(() =>
            {
                var doc = fpdfview.FPDF_LoadMemDocument(nativeBuffer, pdfBytes.Length, password);
                if (doc is null || doc.__Instance == IntPtr.Zero)
                {
                    var err = fpdfview.FPDF_GetLastError();
                    throw new InvalidOperationException($"Could not open '{displayName}': {PdfiumEngine.DescribeLastError(err)}");
                }

                try
                {
                    return BuildSession(doc, nativeBuffer, filePath, displayName);
                }
                catch
                {
                    fpdfview.FPDF_CloseDocument(doc);
                    throw;
                }
            });
        }
        catch
        {
            Marshal.FreeHGlobal(nativeBuffer);
            throw;
        }
    }

    public byte[] SaveToBytes()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return PdfiumEngine.Instance.RunLocked(() =>
        {
            using var ms = new MemoryStream();
            using var fileWrite = new FPDF_FILEWRITE_
            {
                Version = 1,
                WriteBlock = (_, pData, size) =>
                {
                    if (size == 0)
                    {
                        return 1;
                    }

                    int byteCount = checked((int)size);
                    byte[] chunk = new byte[byteCount];
                    Marshal.Copy(pData, chunk, 0, byteCount);
                    ms.Write(chunk, 0, byteCount);
                    return 1;
                }
            };

            if (fpdf_save.FPDF_SaveAsCopy(_document, fileWrite, 0) == 0)
            {
                throw new InvalidOperationException($"Failed to save PDF '{Metadata.FileName}'.");
            }

            return ms.ToArray();
        });
    }

    public void SaveToFile(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            throw new ArgumentException("Target path must not be empty.", nameof(targetPath));
        }

        byte[] bytes = SaveToBytes();
        string fullPath = Path.GetFullPath(targetPath);
        string? dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tempPath = Path.Combine(dir ?? Path.GetTempPath(), $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(tempPath, bytes);
            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }

        Metadata = Metadata with
        {
            FilePath = fullPath,
            FileName = Path.GetFileName(fullPath)
        };
    }

    public void RefreshFontSummary()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        PdfiumEngine.Instance.RunLocked(() =>
        {
            Metadata = Metadata with
            {
                FontEmbedSummary = InspectFontEmbedSummary(_document, Metadata.PageCount)
            };
        });
    }

    public void RefreshPagesAndMetadata()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        PdfiumEngine.Instance.RunLocked(() =>
        {
            int pageCount = fpdfview.FPDF_GetPageCount(_document);
            _pages.Clear();
            for (int i = 0; i < pageCount; i++)
            {
                double w = 0, h = 0;
                if (fpdfview.FPDF_GetPageSizeByIndex(_document, i, ref w, ref h) == 0)
                {
                    w = 612;
                    h = 792;
                }

                _pages.Add(new PdfPageInfo(i, i + 1, w, h, 0));
            }

            Metadata = Metadata with
            {
                PageCount = pageCount,
                FontEmbedSummary = InspectFontEmbedSummary(_document, pageCount)
            };
        });
    }

    private static PdfDocumentSession BuildSession(
        FpdfDocumentT doc,
        IntPtr pinnedMemoryPtr,
        string? filePath,
        string fileName)
    {
        var pageCount = fpdfview.FPDF_GetPageCount(doc);
        int rawVersion = 0;
        var hasVersion = fpdfview.FPDF_GetFileVersion(doc, ref rawVersion) != 0 && rawVersion > 0;
        var pdfVersion = hasVersion
            ? $"PDF {rawVersion / 10}.{rawVersion % 10}"
            : "PDF";

        var title = ReadMetaText(doc, "Title");
        var author = ReadMetaText(doc, "Author");

        var pages = new List<PdfPageInfo>(pageCount);
        for (int i = 0; i < pageCount; i++)
        {
            double widthPts = 0;
            double heightPts = 0;
            if (fpdfview.FPDF_GetPageSizeByIndex(doc, i, ref widthPts, ref heightPts) == 0)
            {
                widthPts = 612;
                heightPts = 792;
            }

            pages.Add(new PdfPageInfo(
                PageIndex: i,
                PageNumber: i + 1,
                WidthPoints: widthPts,
                HeightPoints: heightPts,
                RotationDegrees: 0));
        }

        var fontSummary = InspectFontEmbedSummary(doc, pageCount);
        var outlines = ReadOutlines(doc, null);

        var metadata = new PdfDocumentMetadata(
            FilePath: filePath,
            FileName: fileName,
            PageCount: pageCount,
            PdfVersion: pdfVersion,
            FontEmbedSummary: fontSummary,
            Title: title,
            Author: author);

        return new PdfDocumentSession(doc, pinnedMemoryPtr, metadata, pages, outlines);
    }

    private static string? ReadMetaText(FpdfDocumentT doc, string tag)
    {
        uint len = fpdf_doc.FPDF_GetMetaText(doc, tag, IntPtr.Zero, 0);
        if (len <= 2)
        {
            return null;
        }

        IntPtr buf = Marshal.AllocHGlobal((int)len);
        try
        {
            uint written = fpdf_doc.FPDF_GetMetaText(doc, tag, buf, len);
            if (written <= 2)
            {
                return null;
            }

            var bytes = new byte[written];
            Marshal.Copy(buf, bytes, 0, (int)written);
            var text = Encoding.Unicode.GetString(bytes).TrimEnd('\0').Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static string InspectFontEmbedSummary(FpdfDocumentT doc, int pageCount)
    {
        if (pageCount <= 0)
        {
            return "No Pages";
        }

        int samplePages = Math.Min(pageCount, 3);
        int sampledFonts = 0;
        int embeddedOrSubsetCount = 0;
        var seenFontNames = new HashSet<string>(StringComparer.Ordinal);

        for (int p = 0; p < samplePages; p++)
        {
            var page = fpdfview.FPDF_LoadPage(doc, p);
            if (page is null || page.__Instance == IntPtr.Zero)
            {
                continue;
            }

            try
            {
                var textPage = fpdf_text.FPDFTextLoadPage(page);
                if (textPage is null || textPage.__Instance == IntPtr.Zero)
                {
                    continue;
                }

                try
                {
                    int charCount = fpdf_text.FPDFTextCountChars(textPage);
                    int step = Math.Max(1, charCount / 40);
                    for (int c = 0; c < charCount; c += step)
                    {
                        var fontName = ReadTextCharFontName(textPage, c);
                        if (string.IsNullOrEmpty(fontName) || !seenFontNames.Add(fontName))
                        {
                            continue;
                        }

                        sampledFonts++;
                        if (fontName.Contains('+') || !IsStandard14Font(fontName))
                        {
                            embeddedOrSubsetCount++;
                        }
                    }
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
        }

        if (sampledFonts == 0)
        {
            return "No Text Objects";
        }

        if (embeddedOrSubsetCount == sampledFonts)
        {
            return "Fonts Embedded";
        }

        if (embeddedOrSubsetCount > 0)
        {
            return "Fonts Mixed";
        }

        return "Standard PDF Fonts";
    }

    private static string? ReadTextCharFontName(FpdfTextpageT textPage, int charIndex)
    {
        int flags = 0;
        uint required = fpdf_text.FPDFTextGetFontInfo(textPage, charIndex, IntPtr.Zero, 0, ref flags);
        if (required <= 1)
        {
            return null;
        }

        IntPtr buf = Marshal.AllocHGlobal((int)required);
        try
        {
            uint written = fpdf_text.FPDFTextGetFontInfo(textPage, charIndex, buf, required, ref flags);
            if (written <= 1)
            {
                return null;
            }

            var bytes = new byte[written];
            Marshal.Copy(buf, bytes, 0, (int)written);
            return Encoding.UTF8.GetString(bytes).TrimEnd('\0').Trim();
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static bool IsStandard14Font(string fontName)
    {
        return fontName is "Helvetica" or "Helvetica-Bold" or "Helvetica-Oblique" or "Helvetica-BoldOblique"
            or "Times-Roman" or "Times-Bold" or "Times-Italic" or "Times-BoldItalic"
            or "Courier" or "Courier-Bold" or "Courier-Oblique" or "Courier-BoldOblique"
            or "Symbol" or "ZapfDingbats";
    }

    private static List<PdfOutlineNode> ReadOutlines(FpdfDocumentT doc, FpdfBookmarkT? parent, int depth = 0)
    {
        var list = new List<PdfOutlineNode>();
        if (depth > 16)
        {
            return list;
        }

        var current = fpdf_doc.FPDFBookmarkGetFirstChild(doc, parent);
        while (current is not null && current.__Instance != IntPtr.Zero)
        {
            var title = ReadBookmarkTitle(current) ?? "Untitled";
            int? targetPage = ResolveBookmarkPageIndex(doc, current);
            var children = ReadOutlines(doc, current, depth + 1);
            list.Add(new PdfOutlineNode(title, targetPage, children));

            current = fpdf_doc.FPDFBookmarkGetNextSibling(doc, current);
        }

        return list;
    }

    private static string? ReadBookmarkTitle(FpdfBookmarkT bookmark)
    {
        uint len = fpdf_doc.FPDFBookmarkGetTitle(bookmark, IntPtr.Zero, 0);
        if (len <= 2)
        {
            return null;
        }

        IntPtr buf = Marshal.AllocHGlobal((int)len);
        try
        {
            uint written = fpdf_doc.FPDFBookmarkGetTitle(bookmark, buf, len);
            if (written <= 2)
            {
                return null;
            }

            var bytes = new byte[written];
            Marshal.Copy(buf, bytes, 0, (int)written);
            return Encoding.Unicode.GetString(bytes).TrimEnd('\0').Trim();
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static int? ResolveBookmarkPageIndex(FpdfDocumentT doc, FpdfBookmarkT bookmark)
    {
        var dest = fpdf_doc.FPDFBookmarkGetDest(doc, bookmark);
        if (dest is null || dest.__Instance == IntPtr.Zero)
        {
            var action = fpdf_doc.FPDFBookmarkGetAction(bookmark);
            if (action is not null && action.__Instance != IntPtr.Zero)
            {
                dest = fpdf_doc.FPDFActionGetDest(doc, action);
            }
        }

        if (dest is null || dest.__Instance == IntPtr.Zero)
        {
            return null;
        }

        int pageIndex = fpdf_doc.FPDFDestGetDestPageIndex(doc, dest);
        return pageIndex >= 0 ? pageIndex : null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        PdfiumEngine.Instance.RunLocked(() =>
        {
            if (_document.__Instance != IntPtr.Zero)
            {
                fpdfview.FPDF_CloseDocument(_document);
            }
        });

        if (_pinnedMemoryPtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_pinnedMemoryPtr);
        }
    }
}
