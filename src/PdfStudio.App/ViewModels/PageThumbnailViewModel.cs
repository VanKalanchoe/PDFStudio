using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfStudio.App.Rendering;
using PdfStudio.Core.Documents;

namespace PdfStudio.App.ViewModels;

public sealed partial class PageThumbnailViewModel : ViewModelBase, IDisposable
{
    private static readonly SemaphoreSlim ThumbnailRenderGate = new(1, 1);

    private readonly Func<PdfDocumentSession> _sessionProvider;
    private readonly PdfPageRenderer _renderer;
    private CancellationTokenSource? _renderCts;
    private bool _isLoading;
    private bool _disposed;

    [ObservableProperty]
    private WriteableBitmap? _thumbnailBitmap;

    [ObservableProperty]
    private bool _isSelected;

    public PageThumbnailViewModel(Func<PdfDocumentSession> sessionProvider, PdfPageRenderer renderer, PdfPageInfo pageInfo)
    {
        _sessionProvider = sessionProvider;
        _renderer = renderer;
        PageIndex = pageInfo.PageIndex;
        PageNumber = pageInfo.PageNumber;

        double aspect = pageInfo.WidthPoints > 0 ? pageInfo.HeightPoints / pageInfo.WidthPoints : 1.414;
        DisplayWidth = 112;
        DisplayHeight = Math.Clamp(Math.Round(DisplayWidth * aspect), 72, 200);
    }

    public int PageIndex { get; }
    public int PageNumber { get; }
    public double DisplayWidth { get; }
    public double DisplayHeight { get; }

    public void InvalidateThumbnail()
    {
        CancelPendingRender();
        ThumbnailBitmap?.Dispose();
        ThumbnailBitmap = null;
    }

    public async Task EnsureLoadedAsync(double dpiScale = 1.5)
    {
        if (_disposed || ThumbnailBitmap is not null || _isLoading)
        {
            return;
        }

        _isLoading = true;
        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _renderCts = new CancellationTokenSource();
        var token = _renderCts.Token;

        try
        {
            await ThumbnailRenderGate.WaitAsync(token);
            try
            {
                using var skBitmap = await _renderer.RenderThumbnailAsync(
                    _sessionProvider(),
                    PageIndex,
                    maxWidthPx: (int)DisplayWidth,
                    dpiScale: Math.Clamp(dpiScale, 1.0, 2.5),
                    cancellationToken: token);

                if (token.IsCancellationRequested || _disposed)
                {
                    return;
                }

                var wb = SkiaBitmapAdapter.ToWriteableBitmap(skBitmap);
                if (_disposed)
                {
                    wb.Dispose();
                    return;
                }

                ThumbnailBitmap = wb;
            }
            finally
            {
                ThumbnailRenderGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Ignored when scrolled away before render completed
        }
        catch (ObjectDisposedException)
        {
            // Ignored when document tab is closed during an in-flight render
        }
        finally
        {
            _isLoading = false;
        }
    }

    public void CancelPendingRender()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _renderCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Safe if raced with disposal
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var cts = Interlocked.Exchange(ref _renderCts, null);
        if (cts is not null)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            cts.Dispose();
        }

        ThumbnailBitmap?.Dispose();
        ThumbnailBitmap = null;
    }
}
