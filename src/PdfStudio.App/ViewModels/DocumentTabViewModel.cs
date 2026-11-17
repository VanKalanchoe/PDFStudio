using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfStudio.Core.Documents;
using PdfStudio.Core.Editing;
using PdfStudio.Core.Layout;
using PdfStudio.Core.Ocr;

namespace PdfStudio.App.ViewModels;

public sealed partial class DocumentTabViewModel : ViewModelBase, IDisposable
{
    private const int MaxUndoSteps = 25;

    private readonly ConcurrentDictionary<int, PdfPageLayout> _layouts = new();
    private readonly ConcurrentDictionary<int, Task<PdfPageLayout>> _layoutTasks = new();
    private readonly ConcurrentDictionary<int, IReadOnlyList<PdfAnnotationInfo>> _annotations = new();
    private readonly ConcurrentDictionary<int, IReadOnlyList<PdfImageObjectInfo>> _images = new();
    private readonly List<byte[]> _undoStack = new();
    private readonly List<byte[]> _redoStack = new();
    private int _savedUndoDepth;
    private PdfDocumentSession _session;
    private bool _disposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabTitle))]
    private bool _isDirty;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private int _currentPageIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomPercent))]
    [NotifyPropertyChangedFor(nameof(ZoomPercentText))]
    private double _zoomFactor = 1.0;

    [ObservableProperty]
    private PageThumbnailViewModel? _selectedThumbnail;

    [ObservableProperty]
    private OutlineItemViewModel? _selectedOutlineItem;

    [ObservableProperty]
    private PdfTextBlock? _hoveredTextBlock;

    [ObservableProperty]
    private PdfTextBlock? _selectedTextBlock;

    [ObservableProperty]
    private PdfImageObjectInfo? _selectedImage;

    public DocumentTabViewModel(
        PdfDocumentSession session,
        PdfPageRenderer renderer,
        TextBlockDetector? textBlockDetector = null,
        PdfTextEditor? textEditor = null,
        PdfAnnotationService? annotationService = null,
        PdfImageEditor? imageEditor = null,
        PdfRedactionService? redactionService = null,
        PdfPageOrganizer? pageOrganizer = null,
        PdfOcrService? ocrService = null)
    {
        _session = session;
        Renderer = renderer;
        TextBlockDetector = textBlockDetector ?? new TextBlockDetector();
        TextEditor = textEditor ?? new PdfTextEditor();
        AnnotationService = annotationService ?? new PdfAnnotationService();
        ImageEditor = imageEditor ?? new PdfImageEditor();
        RedactionService = redactionService ?? new PdfRedactionService();
        PageOrganizer = pageOrganizer ?? new PdfPageOrganizer();
        OcrService = ocrService ?? new PdfOcrService(renderer);

        Pages = new ObservableCollection<PageThumbnailViewModel>(
            session.Pages.Select(p => new PageThumbnailViewModel(() => Session, renderer, p)));

        Outlines = new ObservableCollection<OutlineItemViewModel>(
            session.Outlines.Select(o => new OutlineItemViewModel(o)));

        if (Pages.Count > 0)
        {
            Pages[0].IsSelected = true;
            SelectedThumbnail = Pages[0];
        }
    }

    public PdfDocumentSession Session => _session;
    public PdfPageRenderer Renderer { get; }
    public TextBlockDetector TextBlockDetector { get; }
    public PdfTextEditor TextEditor { get; }
    public PdfAnnotationService AnnotationService { get; }
    public PdfImageEditor ImageEditor { get; }
    public PdfRedactionService RedactionService { get; }
    public PdfPageOrganizer PageOrganizer { get; }
    public PdfOcrService OcrService { get; }
    public ObservableCollection<PageThumbnailViewModel> Pages { get; }
    public ObservableCollection<OutlineItemViewModel> Outlines { get; }

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public event EventHandler<int>? LayoutDetected;
    public event EventHandler<int>? PageInvalidated;
    public event EventHandler? DocumentReloaded;

    public PdfPageLayout? GetCachedLayout(int pageIndex) =>
        _layouts.TryGetValue(pageIndex, out var layout) ? layout : null;

    public IReadOnlyList<PdfAnnotationInfo> GetCachedAnnotations(int pageIndex)
    {
        if (_annotations.TryGetValue(pageIndex, out var list))
        {
            return list;
        }

        var loaded = AnnotationService.GetAnnotations(_session, pageIndex);
        _annotations[pageIndex] = loaded;
        return loaded;
    }

    public IReadOnlyList<PdfImageObjectInfo> GetCachedImages(int pageIndex)
    {
        if (_images.TryGetValue(pageIndex, out var list))
        {
            return list;
        }

        var loaded = ImageEditor.GetImages(_session, pageIndex);
        _images[pageIndex] = loaded;
        return loaded;
    }

    public Task<PdfPageLayout> EnsurePageLayoutAsync(int pageIndex, bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!forceRefresh && _layouts.TryGetValue(pageIndex, out var cached))
        {
            return Task.FromResult(cached);
        }

        if (forceRefresh)
        {
            _layouts.TryRemove(pageIndex, out _);
            _layoutTasks.TryRemove(pageIndex, out _);
        }

        return _layoutTasks.GetOrAdd(pageIndex, idx => DetectAndCacheAsync(idx, cancellationToken));
    }

    private async Task<PdfPageLayout> DetectAndCacheAsync(int pageIndex, CancellationToken cancellationToken)
    {
        try
        {
            var layout = await TextBlockDetector.DetectBlocksAsync(Session, pageIndex, cancellationToken);
            if (!_disposed)
            {
                _layouts[pageIndex] = layout;
                LayoutDetected?.Invoke(this, pageIndex);
            }

            return layout;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            var page = Session.Pages[pageIndex];
            return new PdfPageLayout(pageIndex, page.WidthPoints, page.HeightPoints, []);
        }
        finally
        {
            _layoutTasks.TryRemove(pageIndex, out _);
        }
    }

    public string FileName => Session.Metadata.FileName;
    public string? FilePath => Session.Metadata.FilePath;
    public string TabTitle => IsDirty ? $"{FileName} *" : FileName;
    public int PageCount => Session.Metadata.PageCount;
    public bool HasOutlines => Outlines.Count > 0;

    public double ZoomPercent
    {
        get => Math.Round(ZoomFactor * 100.0);
        set
        {
            var clamped = Math.Clamp(value / 100.0, 0.25, 4.0);
            ZoomFactor = clamped;
        }
    }

    public string ZoomPercentText => $"{Math.Round(ZoomFactor * 100.0):0}%";

    public string StatusSummary
    {
        get
        {
            if (PageCount <= 0)
            {
                return $"{Session.Metadata.PdfVersion} • {Session.Metadata.FontEmbedSummary}";
            }

            int idx = Math.Clamp(CurrentPageIndex, 0, PageCount - 1);
            var page = Session.Pages[idx];
            string dims = string.Format(
                CultureInfo.InvariantCulture,
                "{0:0.#} × {1:0.#} in",
                page.WidthInches,
                page.HeightInches);

            return $"Page {idx + 1} of {PageCount} • {dims} • {Session.Metadata.PdfVersion} • {Session.Metadata.FontEmbedSummary}";
        }
    }

    partial void OnCurrentPageIndexChanged(int value)
    {
        if (Pages.Count == 0)
        {
            return;
        }

        int clamped = Math.Clamp(value, 0, Pages.Count - 1);
        for (int i = 0; i < Pages.Count; i++)
        {
            Pages[i].IsSelected = i == clamped;
        }

        var target = Pages[clamped];
        if (!ReferenceEquals(SelectedThumbnail, target))
        {
            SelectedThumbnail = target;
        }

        OnPropertyChanged(nameof(StatusSummary));
    }

    partial void OnSelectedThumbnailChanged(PageThumbnailViewModel? value)
    {
        if (value is not null && value.PageIndex != CurrentPageIndex)
        {
            CurrentPageIndex = value.PageIndex;
            NavigationRequested?.Invoke(this, value.PageIndex);
        }
    }

    partial void OnSelectedOutlineItemChanged(OutlineItemViewModel? value)
    {
        if (value?.TargetPageIndex is int pageIndex && pageIndex >= 0 && pageIndex < PageCount)
        {
            CurrentPageIndex = pageIndex;
            NavigationRequested?.Invoke(this, pageIndex);
        }
    }

    public event EventHandler<int>? NavigationRequested;
    public event EventHandler? FitWidthRequested;
    public event EventHandler? FitPageRequested;

    public void RequestNavigateToPage(int pageIndex)
    {
        if (PageCount <= 0)
        {
            return;
        }

        int clamped = Math.Clamp(pageIndex, 0, PageCount - 1);
        CurrentPageIndex = clamped;
        NavigationRequested?.Invoke(this, clamped);
    }

    public void RequestFitWidth() => FitWidthRequested?.Invoke(this, EventArgs.Empty);
    public void RequestFitPage() => FitPageRequested?.Invoke(this, EventArgs.Empty);

    public async Task<PdfTextEditResult> ApplyTextEditAsync(PdfTextEditRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        byte[] beforeSnapshot = await Task.Run(() => _session.SaveToBytes());
        var result = await Task.Run(() => TextEditor.ApplyTextEdit(_session, request));
        if (!result.Success)
        {
            return result;
        }

        PushUndoSnapshot(beforeSnapshot);
        _session.RefreshFontSummary();
        IsDirty = true;
        InvalidatePage(request.OriginalBlock.PageIndex);
        OnPropertyChanged(nameof(StatusSummary));
        return result;
    }

    public void InvalidatePage(int pageIndex)
    {
        _layouts.TryRemove(pageIndex, out _);
        _layoutTasks.TryRemove(pageIndex, out _);
        _annotations.TryRemove(pageIndex, out _);
        _images.TryRemove(pageIndex, out _);

        if (pageIndex >= 0 && pageIndex < Pages.Count)
        {
            Pages[pageIndex].InvalidateThumbnail();
            _ = Pages[pageIndex].EnsureLoadedAsync(1.5);
        }

        PageInvalidated?.Invoke(this, pageIndex);
    }

    public async Task AddHighlightAsync(int pageIndex, PdfRect bounds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] snapshot = await Task.Run(() => _session.SaveToBytes());
        await Task.Run(() => AnnotationService.AddHighlight(_session, pageIndex, bounds));
        PushUndoSnapshot(snapshot);
        IsDirty = true;
        InvalidatePage(pageIndex);
    }

    public async Task AddCommentAsync(int pageIndex, double ptX, double ptY, string commentText)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] snapshot = await Task.Run(() => _session.SaveToBytes());
        await Task.Run(() => AnnotationService.AddComment(_session, pageIndex, ptX, ptY, commentText));
        PushUndoSnapshot(snapshot);
        IsDirty = true;
        InvalidatePage(pageIndex);
    }

    public async Task DeleteAnnotationAsync(int pageIndex, int annotIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] snapshot = await Task.Run(() => _session.SaveToBytes());
        bool deleted = await Task.Run(() => AnnotationService.DeleteAnnotation(_session, pageIndex, annotIndex));
        if (!deleted)
        {
            return;
        }

        PushUndoSnapshot(snapshot);
        IsDirty = true;
        InvalidatePage(pageIndex);
    }

    public async Task<bool> InsertImageAsync(int pageIndex, byte[] imageBytes, PdfRect targetBounds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] snapshot = await Task.Run(() => _session.SaveToBytes());
        bool ok = await Task.Run(() => ImageEditor.InsertImage(_session, pageIndex, imageBytes, targetBounds));
        if (!ok)
        {
            return false;
        }

        PushUndoSnapshot(snapshot);
        IsDirty = true;
        InvalidatePage(pageIndex);

        var pageImages = GetCachedImages(pageIndex);
        SelectedImage = pageImages.Count > 0 ? pageImages[^1] : null;
        return true;
    }

    public async Task<bool> SetImageBoundsAsync(int pageIndex, int objectIndex, PdfRect newBounds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] snapshot = await Task.Run(() => _session.SaveToBytes());
        bool ok = await Task.Run(() => ImageEditor.SetImageBounds(_session, pageIndex, objectIndex, newBounds));
        if (!ok)
        {
            return false;
        }

        PushUndoSnapshot(snapshot);
        IsDirty = true;
        InvalidatePage(pageIndex);

        var refreshed = GetCachedImages(pageIndex);
        SelectedImage = refreshed.FirstOrDefault(img => img.ObjectIndex == objectIndex)
            ?? refreshed.LastOrDefault();
        return true;
    }

    public async Task<bool> DeleteImageAsync(int pageIndex, int objectIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] snapshot = await Task.Run(() => _session.SaveToBytes());
        bool ok = await Task.Run(() => ImageEditor.DeleteImage(_session, pageIndex, objectIndex));
        if (!ok)
        {
            return false;
        }

        if (SelectedImage?.PageIndex == pageIndex && SelectedImage?.ObjectIndex == objectIndex)
        {
            SelectedImage = null;
        }

        PushUndoSnapshot(snapshot);
        IsDirty = true;
        InvalidatePage(pageIndex);
        return true;
    }

    public async Task<PdfRedactionResult> ApplyRedactionAsync(int pageIndex, PdfRect redactBounds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] snapshot = await Task.Run(() => _session.SaveToBytes());
        var result = await Task.Run(() => RedactionService.ApplyRedaction(_session, pageIndex, redactBounds));
        if (!result.Success)
        {
            return result;
        }

        PushUndoSnapshot(snapshot);
        IsDirty = true;
        InvalidatePage(pageIndex);
        return result;
    }

    public async Task<PdfOcrPageResult> RunOcrOnPageAsync(int pageIndex, string language = "eng")
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] snapshot = await Task.Run(() => _session.SaveToBytes());
        var result = await OcrService.RunOcrOnPageAsync(_session, pageIndex, language, embedInvisibleTextLayer: true);
        if (result.EmbeddedSearchableText)
        {
            PushUndoSnapshot(snapshot);
            IsDirty = true;
            InvalidatePage(pageIndex);
            _ = EnsurePageLayoutAsync(pageIndex, forceRefresh: true);
        }

        return result;
    }

    public async Task InsertBlankPageAsync(int insertAtIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] snapshot = await Task.Run(() => _session.SaveToBytes());
        double w = Pages.Count > 0 ? _session.Pages[Math.Clamp(CurrentPageIndex, 0, Pages.Count - 1)].WidthPoints : 612;
        double h = Pages.Count > 0 ? _session.Pages[Math.Clamp(CurrentPageIndex, 0, Pages.Count - 1)].HeightPoints : 792;
        await Task.Run(() => PageOrganizer.InsertBlankPage(_session, insertAtIndex, w, h));
        PushUndoSnapshot(snapshot);
        IsDirty = true;
        RebuildPagesAfterStructureChange(Math.Clamp(insertAtIndex, 0, _session.Pages.Count - 1));
    }

    public async Task<bool> DeletePageAsync(int pageIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_session.Pages.Count <= 1)
        {
            return false;
        }

        byte[] snapshot = await Task.Run(() => _session.SaveToBytes());
        await Task.Run(() => PageOrganizer.DeletePage(_session, pageIndex));
        PushUndoSnapshot(snapshot);
        IsDirty = true;
        RebuildPagesAfterStructureChange(Math.Clamp(pageIndex, 0, _session.Pages.Count - 1));
        return true;
    }

    public async Task<bool> MovePageAsync(int fromIndex, int toIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (fromIndex == toIndex || fromIndex < 0 || fromIndex >= _session.Pages.Count || toIndex < 0 || toIndex >= _session.Pages.Count)
        {
            return false;
        }

        byte[] snapshot = await Task.Run(() => _session.SaveToBytes());
        var reordered = await Task.Run(() => PageOrganizer.ReorderPage(_session, fromIndex, toIndex));
        var oldSession = _session;
        _session = reordered;
        oldSession.Dispose();

        PushUndoSnapshot(snapshot);
        IsDirty = true;
        RebuildPagesAfterStructureChange(toIndex);
        return true;
    }

    public async Task<int> MergePdfAsync(byte[] otherPdfBytes, int insertAtIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] snapshot = await Task.Run(() => _session.SaveToBytes());
        int count = await Task.Run(() => PageOrganizer.MergePdf(_session, otherPdfBytes, insertAtIndex));
        if (count > 0)
        {
            PushUndoSnapshot(snapshot);
            IsDirty = true;
            RebuildPagesAfterStructureChange(Math.Clamp(insertAtIndex, 0, _session.Pages.Count - 1));
        }

        return count;
    }

    private void RebuildPagesAfterStructureChange(int targetPageIndex)
    {
        _layouts.Clear();
        _layoutTasks.Clear();
        _annotations.Clear();
        _images.Clear();
        HoveredTextBlock = null;
        SelectedTextBlock = null;
        SelectedImage = null;

        var oldThumbs = Pages.ToArray();
        Pages.Clear();
        foreach (var t in oldThumbs)
        {
            t.Dispose();
        }

        foreach (var p in _session.Pages)
        {
            Pages.Add(new PageThumbnailViewModel(() => Session, Renderer, p));
        }

        CurrentPageIndex = Math.Clamp(targetPageIndex, 0, Math.Max(0, Pages.Count - 1));
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(StatusSummary));
        DocumentReloaded?.Invoke(this, EventArgs.Empty);
        NavigationRequested?.Invoke(this, CurrentPageIndex);
    }

    public async Task<bool> UndoAsync()
    {
        if (!CanUndo || _disposed)
        {
            return false;
        }

        byte[] targetBytes = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);

        byte[] redoBytes = await Task.Run(() => _session.SaveToBytes());
        _redoStack.Add(redoBytes);

        await ReloadSessionFromBytesAsync(targetBytes);
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        return true;
    }

    public async Task<bool> RedoAsync()
    {
        if (!CanRedo || _disposed)
        {
            return false;
        }

        byte[] targetBytes = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);

        byte[] undoBytes = await Task.Run(() => _session.SaveToBytes());
        _undoStack.Add(undoBytes);

        await ReloadSessionFromBytesAsync(targetBytes);
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        return true;
    }

    public async Task SaveToFileAsync(string targetPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await Task.Run(() => _session.SaveToFile(targetPath));
        _savedUndoDepth = _undoStack.Count;
        IsDirty = false;
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(FilePath));
        OnPropertyChanged(nameof(TabTitle));
        OnPropertyChanged(nameof(StatusSummary));
    }

    private void PushUndoSnapshot(byte[] snapshot)
    {
        _undoStack.Add(snapshot);
        if (_undoStack.Count > MaxUndoSteps)
        {
            _undoStack.RemoveAt(0);
            _savedUndoDepth--;
        }

        _redoStack.Clear();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    private async Task ReloadSessionFromBytesAsync(byte[] pdfBytes)
    {
        string? originalPath = _session.Metadata.FilePath;
        string displayName = _session.Metadata.FileName;

        var replacement = await Task.Run(() =>
            PdfDocumentSession.OpenBytesWithFilePath(pdfBytes, originalPath, displayName));

        var oldSession = _session;
        _session = replacement;
        oldSession.Dispose();

        _layouts.Clear();
        _layoutTasks.Clear();
        _annotations.Clear();
        _images.Clear();
        HoveredTextBlock = null;
        SelectedTextBlock = null;
        SelectedImage = null;

        if (Pages.Count != _session.Pages.Count)
        {
            var oldThumbs = Pages.ToArray();
            Pages.Clear();
            foreach (var t in oldThumbs)
            {
                t.Dispose();
            }

            foreach (var p in _session.Pages)
            {
                Pages.Add(new PageThumbnailViewModel(() => Session, Renderer, p));
            }
        }
        else
        {
            foreach (var thumb in Pages)
            {
                thumb.InvalidateThumbnail();
            }
        }

        CurrentPageIndex = Math.Clamp(CurrentPageIndex, 0, Math.Max(0, Pages.Count - 1));
        if (CurrentPageIndex >= 0 && CurrentPageIndex < Pages.Count)
        {
            Pages[CurrentPageIndex].IsSelected = true;
            SelectedThumbnail = Pages[CurrentPageIndex];
            _ = Pages[CurrentPageIndex].EnsureLoadedAsync(1.5);
        }

        IsDirty = _undoStack.Count != _savedUndoDepth;
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(StatusSummary));
        DocumentReloaded?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var pagesSnapshot = Pages.ToArray();
        Pages.Clear();
        foreach (var page in pagesSnapshot)
        {
            page.Dispose();
        }

        Session.Dispose();
    }
}
