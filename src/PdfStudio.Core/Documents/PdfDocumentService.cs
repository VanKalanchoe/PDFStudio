namespace PdfStudio.Core.Documents;

public interface IPdfDocumentService
{
    Task<PdfDocumentSession> OpenAsync(string filePath, string? password = null, CancellationToken cancellationToken = default);
    Task<PdfDocumentSession> OpenAsync(byte[] pdfBytes, string displayName, string? password = null, CancellationToken cancellationToken = default);
}

public sealed class PdfDocumentService : IPdfDocumentService
{
    public Task<PdfDocumentSession> OpenAsync(
        string filePath,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => PdfDocumentSession.OpenFile(filePath, password), cancellationToken);
    }

    public Task<PdfDocumentSession> OpenAsync(
        byte[] pdfBytes,
        string displayName,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => PdfDocumentSession.OpenBytes(pdfBytes, displayName, password), cancellationToken);
    }
}
