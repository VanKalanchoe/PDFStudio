using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PdfStudio.App.Models;
using PdfStudio.App.Rendering;
using PdfStudio.App.ViewModels;
using PdfStudio.Core.Documents;
using PdfStudio.Core.Editing;
using PdfStudio.Core.Layout;

namespace PdfStudio.App.Controls;

/// <summary>
/// Interactive DPI-aware PDF viewport supporting smooth zoom, pan, continuous page scrolling,
/// LRU bitmap caching, and interactive text-block hover/selection overlays.
/// </summary>
public sealed class PdfViewportControl : Control
{
    private const double PointsToDips = 96.0 / 72.0;
    private const double PageGap = 20.0;
    private const double OuterPadding = 28.0;
    private const int MaxCachedPages = 10;
    private const int MaxRenderDimensionPx = 4096;

    public static readonly StyledProperty<DocumentTabViewModel?> DocumentTabProperty =
        AvaloniaProperty.Register<PdfViewportControl, DocumentTabViewModel?>(nameof(DocumentTab));

    public static readonly StyledProperty<EditorTool> ActiveToolProperty =
        AvaloniaProperty.Register<PdfViewportControl, EditorTool>(nameof(ActiveTool), defaultValue: EditorTool.Select);

    public static readonly StyledProperty<PdfTextBlock?> EditingBlockProperty =
        AvaloniaProperty.Register<PdfViewportControl, PdfTextBlock?>(nameof(EditingBlock));

    private sealed class CachedPageBitmap : IDisposable
    {
        public required WriteableBitmap Bitmap { get; init; }
        public required double RenderedZoom { get; init; }
        public required double RenderedDpi { get; init; }
        public long LastUsedTick { get; set; }

        public void Dispose() => Bitmap.Dispose();
    }

    private readonly Dictionary<int, CachedPageBitmap> _pageCache = new();
    private readonly HashSet<int> _inFlightPages = new();
    private readonly DispatcherTimer _zoomRenderDebounce;
    private readonly List<Rect> _cachedPageRects = new();

    private CancellationTokenSource? _renderCts;
    private double _cachedRectsZoom = -1;
    private double _cachedRectsViewportWidth = -1;
    private double _cachedTotalWidth;
    private double _cachedTotalHeight;
    private DocumentTabViewModel? _cachedRectsTab;
    private double _scrollX;
    private double _scrollY;
    private bool _isPanning;
    private Point _lastPanPoint;
    private long _tickCounter;
    private bool _suppressPageSync;

    private enum ImageDragMode
    {
        None,
        Move,
        ResizeNW,
        ResizeN,
        ResizeNE,
        ResizeE,
        ResizeSE,
        ResizeS,
        ResizeSW,
        ResizeW
    }

    private bool _isDraggingToolBox;
    private int _dragPageIndex = -1;
    private Point _dragStartScreen;
    private Point _dragCurrentScreen;
    private Rect _dragPageScreenRect;

    private ImageDragMode _imageDragMode;
    private PdfImageObjectInfo? _draggingImage;
    private Point _imageDragStartScreen;
    private PdfRect _imageDragOriginalBounds;
    private PdfRect _imageDragCurrentBounds;
    private PdfImageObjectInfo? _hoveredImage;

    private static readonly IBrush PageShadowOuterBrush = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0));
    private static readonly IBrush PageShadowInnerBrush = new SolidColorBrush(Color.FromArgb(72, 0, 0, 0));
    private static readonly IBrush PagePaperBrush = Brushes.White;
    private static readonly IPen PageBorderPen = new Pen(new SolidColorBrush(Color.FromRgb(42, 49, 68)), 1);

    private static readonly IPen BlockGuidePen = new Pen(new SolidColorBrush(Color.FromArgb(48, 148, 157, 175)), 0.75);
    private static readonly IBrush BlockHoverFillBrush = new SolidColorBrush(Color.FromArgb(20, 66, 135, 245));
    private static readonly IPen BlockHoverPen = new Pen(new SolidColorBrush(Color.FromArgb(180, 66, 135, 245)), 1.0);
    private static readonly IBrush BlockSelectFillBrush = new SolidColorBrush(Color.FromArgb(14, 66, 135, 245));
    private static readonly IPen BlockSelectPen = new Pen(new SolidColorBrush(Color.FromRgb(66, 135, 245)), 1.5);
    private static readonly IBrush HandleFillBrush = Brushes.White;
    private static readonly IPen HandleBorderPen = new Pen(new SolidColorBrush(Color.FromRgb(66, 135, 245)), 1.2);

    private static readonly IBrush HighlightPreviewFill = new SolidColorBrush(Color.FromArgb(95, 245, 209, 66));
    private static readonly IPen HighlightPreviewPen = new Pen(new SolidColorBrush(Color.FromRgb(217, 157, 57)), 1.2);
    private static readonly IBrush RedactPreviewFill = new SolidColorBrush(Color.FromArgb(72, 213, 79, 85));
    private static readonly IPen RedactPreviewPen = new Pen(new SolidColorBrush(Color.FromRgb(213, 79, 85)), 1.5);
    private static readonly IBrush ImagePreviewFill = new SolidColorBrush(Color.FromArgb(48, 66, 135, 245));
    private static readonly IPen ImagePreviewPen = new Pen(new SolidColorBrush(Color.FromRgb(66, 135, 245)), 1.4);
    private static readonly IBrush CommentPinFill = new SolidColorBrush(Color.FromRgb(217, 157, 57));
    private static readonly IPen CommentPinPen = new Pen(new SolidColorBrush(Color.FromRgb(120, 78, 18)), 1.2);

    static PdfViewportControl()
    {
        AffectsRender<PdfViewportControl>(DocumentTabProperty, ActiveToolProperty, EditingBlockProperty);
    }

    public PdfViewportControl()
    {
        ClipToBounds = true;
        Focusable = true;
        _zoomRenderDebounce = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(90)
        };
        _zoomRenderDebounce.Tick += (_, _) =>
        {
            _zoomRenderDebounce.Stop();
            TriggerVisiblePageRenders(forceRefresh: false);
        };
    }

    public DocumentTabViewModel? DocumentTab
    {
        get => GetValue(DocumentTabProperty);
        set => SetValue(DocumentTabProperty, value);
    }

    public EditorTool ActiveTool
    {
        get => GetValue(ActiveToolProperty);
        set => SetValue(ActiveToolProperty, value);
    }

    public PdfTextBlock? EditingBlock
    {
        get => GetValue(EditingBlockProperty);
        set => SetValue(EditingBlockProperty, value);
    }

    public event EventHandler? ViewportTransformChanged;
    public event EventHandler<PdfTextBlock>? BlockEditRequested;
    public event EventHandler? CanvasBackgroundClicked;
    public event EventHandler<(int PageIndex, PdfRect Bounds)>? HighlightRequested;
    public event EventHandler<(int PageIndex, PdfRect Bounds)>? RedactionRequested;
    public event EventHandler<(int PageIndex, PdfRect Bounds)>? ImagePlacementRequested;
    public event EventHandler<(int PageIndex, double PtX, double PtY, Point ScreenPos)>? CommentRequested;
    public event EventHandler<(int PageIndex, PdfAnnotationInfo Annotation, Point ScreenPos)>? CommentPinClicked;
    public event EventHandler<(int PageIndex, int ObjectIndex, PdfRect NewBounds)>? ImageBoundsChanged;

    public bool TryGetBlockScreenRect(int pageIndex, PdfRect pageBoundsPts, out Rect screenRect, out double scale)
    {
        screenRect = default;
        scale = 1.0;

        var tab = DocumentTab;
        if (tab is null || pageIndex < 0 || pageIndex >= tab.PageCount)
        {
            return false;
        }

        var rects = ComputePageRects(tab, out _, out _);
        if (pageIndex >= rects.Count)
        {
            return false;
        }

        var docRect = rects[pageIndex];
        scale = tab.ZoomFactor * PointsToDips;

        double pageScreenX = Math.Round(docRect.X - _scrollX);
        double pageScreenY = Math.Round(docRect.Y - _scrollY);

        screenRect = new Rect(
            pageScreenX + (pageBoundsPts.Left * scale),
            pageScreenY + (pageBoundsPts.Top * scale),
            Math.Max(24, pageBoundsPts.Width * scale),
            Math.Max(18, pageBoundsPts.Height * scale));
        return true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == DocumentTabProperty)
        {
            if (change.OldValue is DocumentTabViewModel oldTab)
            {
                UnsubscribeTab(oldTab);
            }

            ResetViewportState();

            if (change.NewValue is DocumentTabViewModel newTab)
            {
                SubscribeTab(newTab);
                ScrollToPage(newTab.CurrentPageIndex, updateTabIndex: false);
                TriggerVisiblePageRenders(forceRefresh: true);
            }

            InvalidateVisual();
        }
        else if (change.Property == ActiveToolProperty)
        {
            if (ActiveTool == EditorTool.EditText)
            {
                TriggerVisiblePageRenders(forceRefresh: false);
            }

            Cursor = Cursor.Default;
            InvalidateVisual();
        }
        else if (change.Property == BoundsProperty)
        {
            ClampScrollOffsets();
            _zoomRenderDebounce.Stop();
            _zoomRenderDebounce.Start();
            ViewportTransformChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
        }
    }

    private void SubscribeTab(DocumentTabViewModel tab)
    {
        tab.PropertyChanged += OnTabPropertyChanged;
        tab.NavigationRequested += OnTabNavigationRequested;
        tab.FitWidthRequested += OnFitWidthRequested;
        tab.FitPageRequested += OnFitPageRequested;
        tab.LayoutDetected += OnTabLayoutDetected;
        tab.PageInvalidated += OnTabPageInvalidated;
        tab.DocumentReloaded += OnTabDocumentReloaded;
    }

    private void UnsubscribeTab(DocumentTabViewModel tab)
    {
        tab.PropertyChanged -= OnTabPropertyChanged;
        tab.NavigationRequested -= OnTabNavigationRequested;
        tab.FitWidthRequested -= OnFitWidthRequested;
        tab.FitPageRequested -= OnFitPageRequested;
        tab.LayoutDetected -= OnTabLayoutDetected;
        tab.PageInvalidated -= OnTabPageInvalidated;
        tab.DocumentReloaded -= OnTabDocumentReloaded;
    }

    private void OnTabPageInvalidated(object? sender, int pageIndex)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_pageCache.TryGetValue(pageIndex, out var old))
            {
                old.Dispose();
                _pageCache.Remove(pageIndex);
            }

            TriggerVisiblePageRenders(forceRefresh: false);
            InvalidateVisual();
        });
    }

    private void OnTabDocumentReloaded(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var entry in _pageCache.Values)
            {
                entry.Dispose();
            }

            _pageCache.Clear();
            _inFlightPages.Clear();
            TriggerVisiblePageRenders(forceRefresh: true);
            ViewportTransformChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
        });
    }

    private void OnTabLayoutDetected(object? sender, int pageIndex)
    {
        Dispatcher.UIThread.Post(InvalidateVisual);
    }

    private void OnTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentTabViewModel.ZoomFactor))
        {
            ClampScrollOffsets();
            ViewportTransformChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            _zoomRenderDebounce.Stop();
            _zoomRenderDebounce.Start();
        }
        else if (e.PropertyName is nameof(DocumentTabViewModel.SelectedTextBlock)
                 or nameof(DocumentTabViewModel.HoveredTextBlock)
                 or nameof(DocumentTabViewModel.SelectedImage))
        {
            InvalidateVisual();
        }
    }

    private void OnTabNavigationRequested(object? sender, int pageIndex)
    {
        ScrollToPage(pageIndex, updateTabIndex: false);
    }

    private void OnFitWidthRequested(object? sender, EventArgs e)
    {
        if (DocumentTab is null || DocumentTab.PageCount == 0 || Bounds.Width <= 40)
        {
            return;
        }

        int idx = Math.Clamp(DocumentTab.CurrentPageIndex, 0, DocumentTab.PageCount - 1);
        var page = DocumentTab.Session.Pages[idx];
        double baseWidthDip = page.WidthPoints * PointsToDips;
        if (baseWidthDip <= 0)
        {
            return;
        }

        double available = Math.Max(120, Bounds.Width - (OuterPadding * 2));
        DocumentTab.ZoomFactor = Math.Clamp(available / baseWidthDip, 0.25, 4.0);
    }

    private void OnFitPageRequested(object? sender, EventArgs e)
    {
        if (DocumentTab is null || DocumentTab.PageCount == 0 || Bounds.Width <= 40 || Bounds.Height <= 40)
        {
            return;
        }

        int idx = Math.Clamp(DocumentTab.CurrentPageIndex, 0, DocumentTab.PageCount - 1);
        var page = DocumentTab.Session.Pages[idx];
        double baseW = page.WidthPoints * PointsToDips;
        double baseH = page.HeightPoints * PointsToDips;
        if (baseW <= 0 || baseH <= 0)
        {
            return;
        }

        double availW = Math.Max(120, Bounds.Width - (OuterPadding * 2));
        double availH = Math.Max(120, Bounds.Height - (OuterPadding * 2));
        DocumentTab.ZoomFactor = Math.Clamp(Math.Min(availW / baseW, availH / baseH), 0.25, 4.0);
        ScrollToPage(idx, updateTabIndex: false);
    }

    private void ResetViewportState()
    {
        _zoomRenderDebounce.Stop();
        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _renderCts = null;
        _inFlightPages.Clear();

        foreach (var entry in _pageCache.Values)
        {
            entry.Dispose();
        }

        _pageCache.Clear();
        _cachedPageRects.Clear();
        _cachedRectsTab = null;
        _scrollX = 0;
        _scrollY = 0;
        _imageDragMode = ImageDragMode.None;
        _draggingImage = null;
        _hoveredImage = null;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (DocumentTab is not null)
        {
            UnsubscribeTab(DocumentTab);
        }

        ResetViewportState();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (DocumentTab is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsLeftButtonPressed)
        {
            // Check comment pins first when Comment or Select is active
            if ((ActiveTool == EditorTool.Comment || ActiveTool == EditorTool.Select) &&
                TryHitTestCommentPin(point.Position, out int pinPage, out var pinAnnot))
            {
                CommentPinClicked?.Invoke(this, (pinPage, pinAnnot, point.Position));
                e.Handled = true;
                return;
            }

            if (ActiveTool is EditorTool.Select or EditorTool.EditText &&
                TryBeginImageInteraction(point.Position, e.Pointer))
            {
                e.Handled = true;
                return;
            }

            if (ActiveTool == EditorTool.EditText)
            {
                var hit = HitTestTextBlock(point.Position, out int hitPageIndex);
                if (hit is not null)
                {
                    DocumentTab.SelectedImage = null;
                    DocumentTab.CurrentPageIndex = hitPageIndex;
                    DocumentTab.SelectedTextBlock = hit;
                    BlockEditRequested?.Invoke(this, hit);
                    InvalidateVisual();
                    e.Handled = true;
                    return;
                }

                CanvasBackgroundClicked?.Invoke(this, EventArgs.Empty);
                DocumentTab.SelectedTextBlock = null;
                DocumentTab.SelectedImage = null;
                InvalidateVisual();
            }
            else if (ActiveTool is EditorTool.Highlight or EditorTool.Redact or EditorTool.AddImage)
            {
                CanvasBackgroundClicked?.Invoke(this, EventArgs.Empty);
                DocumentTab.SelectedImage = null;
                if (TryHitTestPage(point.Position, out int dragPageIdx, out var pageScreenRect))
                {
                    _isDraggingToolBox = true;
                    _dragPageIndex = dragPageIdx;
                    _dragPageScreenRect = pageScreenRect;
                    _dragStartScreen = ClampPointToRect(point.Position, pageScreenRect);
                    _dragCurrentScreen = _dragStartScreen;
                    e.Pointer.Capture(this);
                    InvalidateVisual();
                    e.Handled = true;
                    return;
                }
            }
            else if (ActiveTool == EditorTool.Comment)
            {
                CanvasBackgroundClicked?.Invoke(this, EventArgs.Empty);
                DocumentTab.SelectedImage = null;
                if (TryHitTestPage(point.Position, out int cPageIdx, out var pageScreenRect))
                {
                    double scale = DocumentTab.ZoomFactor * PointsToDips;
                    double ptX = (point.Position.X - pageScreenRect.X) / scale;
                    double ptY = (point.Position.Y - pageScreenRect.Y) / scale;
                    CommentRequested?.Invoke(this, (cPageIdx, ptX, ptY, point.Position));
                    e.Handled = true;
                    return;
                }
            }
            else
            {
                DocumentTab.SelectedImage = null;
                CanvasBackgroundClicked?.Invoke(this, EventArgs.Empty);
                InvalidateVisual();
            }
        }

        if (point.Properties.IsLeftButtonPressed || point.Properties.IsMiddleButtonPressed)
        {
            _isPanning = true;
            _lastPanPoint = point.Position;
            Cursor = new Cursor(StandardCursorType.SizeAll);
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);

        if (_imageDragMode != ImageDragMode.None && _draggingImage is not null && DocumentTab is not null)
        {
            UpdateImageDragBounds(pos);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (_isDraggingToolBox)
        {
            _dragCurrentScreen = ClampPointToRect(pos, _dragPageScreenRect);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (_isPanning)
        {
            var delta = pos - _lastPanPoint;
            _lastPanPoint = pos;

            _scrollX -= delta.X;
            _scrollY -= delta.Y;
            ClampScrollOffsets();
            SyncCurrentPageFromScroll();
            ViewportTransformChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            TriggerVisiblePageRenders(forceRefresh: false);
            e.Handled = true;
            return;
        }

        if (ActiveTool == EditorTool.Select && DocumentTab is not null)
        {
            UpdateSelectHoverCursor(pos);
        }
        else if (ActiveTool == EditorTool.EditText && DocumentTab is not null)
        {
            var hit = HitTestTextBlock(pos, out _);
            if (!Equals(DocumentTab.HoveredTextBlock, hit))
            {
                DocumentTab.HoveredTextBlock = hit;
                InvalidateVisual();
            }

            if (hit is not null)
            {
                _hoveredImage = null;
                Cursor = new Cursor(StandardCursorType.Ibeam);
            }
            else
            {
                UpdateSelectHoverCursor(pos);
            }
        }
        else if (ActiveTool is EditorTool.Highlight or EditorTool.Redact or EditorTool.AddImage or EditorTool.Comment)
        {
            Cursor = new Cursor(StandardCursorType.Cross);
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hoveredImage is not null)
        {
            _hoveredImage = null;
            InvalidateVisual();
        }

        if (DocumentTab is not null && DocumentTab.HoveredTextBlock is not null)
        {
            DocumentTab.HoveredTextBlock = null;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_imageDragMode != ImageDragMode.None && _draggingImage is not null)
        {
            var img = _draggingImage;
            var original = _imageDragOriginalBounds;
            var updated = _imageDragCurrentBounds;
            _imageDragMode = ImageDragMode.None;
            _draggingImage = null;
            Cursor = Cursor.Default;
            e.Pointer.Capture(null);

            bool changed = Math.Abs(updated.Left - original.Left) >= 0.5
                        || Math.Abs(updated.Top - original.Top) >= 0.5
                        || Math.Abs(updated.Width - original.Width) >= 0.5
                        || Math.Abs(updated.Height - original.Height) >= 0.5;
            if (changed)
            {
                ImageBoundsChanged?.Invoke(this, (img.PageIndex, img.ObjectIndex, updated));
            }

            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (_isDraggingToolBox && DocumentTab is not null)
        {
            _isDraggingToolBox = false;
            e.Pointer.Capture(null);

            double scale = DocumentTab.ZoomFactor * PointsToDips;
            double minX = Math.Min(_dragStartScreen.X, _dragCurrentScreen.X);
            double maxX = Math.Max(_dragStartScreen.X, _dragCurrentScreen.X);
            double minY = Math.Min(_dragStartScreen.Y, _dragCurrentScreen.Y);
            double maxY = Math.Max(_dragStartScreen.Y, _dragCurrentScreen.Y);

            double wPts = (maxX - minX) / scale;
            double hPts = (maxY - minY) / scale;
            double ptX = (minX - _dragPageScreenRect.X) / scale;
            double ptY = (minY - _dragPageScreenRect.Y) / scale;

            if (ActiveTool == EditorTool.AddImage && wPts < 8 && hPts < 8)
            {
                // Single click in AddImage places a default 180pt wide box at cursor
                ImagePlacementRequested?.Invoke(this, (_dragPageIndex, new PdfRect(ptX, ptY, ptX + 180, ptY + 135)));
            }
            else if (wPts >= 4 && hPts >= 4)
            {
                var pdfRect = new PdfRect(ptX, ptY, ptX + wPts, ptY + hPts);
                if (ActiveTool == EditorTool.Highlight)
                {
                    HighlightRequested?.Invoke(this, (_dragPageIndex, pdfRect));
                }
                else if (ActiveTool == EditorTool.Redact)
                {
                    RedactionRequested?.Invoke(this, (_dragPageIndex, pdfRect));
                }
                else if (ActiveTool == EditorTool.AddImage)
                {
                    ImagePlacementRequested?.Invoke(this, (_dragPageIndex, pdfRect));
                }
            }

            _dragPageIndex = -1;
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (_isPanning)
        {
            _isPanning = false;
            Cursor = Cursor.Default;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (DocumentTab is null)
        {
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            double oldZoom = DocumentTab.ZoomFactor;
            double factor = e.Delta.Y > 0 ? 1.12 : 1.0 / 1.12;
            double newZoom = Math.Clamp(oldZoom * factor, 0.25, 4.0);
            if (Math.Abs(newZoom - oldZoom) > 0.0001)
            {
                var cursor = e.GetPosition(this);
                double docPointX = (_scrollX + cursor.X) / oldZoom;
                double docPointY = (_scrollY + cursor.Y) / oldZoom;

                DocumentTab.ZoomFactor = newZoom;

                _scrollX = (docPointX * newZoom) - cursor.X;
                _scrollY = (docPointY * newZoom) - cursor.Y;
                ClampScrollOffsets();
                ViewportTransformChanged?.Invoke(this, EventArgs.Empty);
                InvalidateVisual();
            }

            e.Handled = true;
            return;
        }

        const double scrollStep = 64.0;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            _scrollX -= e.Delta.Y * scrollStep;
        }
        else
        {
            _scrollY -= e.Delta.Y * scrollStep;
            _scrollX -= e.Delta.X * scrollStep;
        }

        ClampScrollOffsets();
        SyncCurrentPageFromScroll();
        ViewportTransformChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        TriggerVisiblePageRenders(forceRefresh: false);
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var tab = DocumentTab;
        if (tab is null || tab.PageCount == 0)
        {
            return;
        }

        var rects = ComputePageRects(tab, out _, out _);
        double viewportTop = _scrollY;
        double viewportBottom = _scrollY + Bounds.Height;

        using var clip = context.PushClip(new Rect(Bounds.Size));

        for (int i = 0; i < rects.Count; i++)
        {
            var docRect = rects[i];
            if (docRect.Bottom < viewportTop - 40 || docRect.Top > viewportBottom + 40)
            {
                continue;
            }

            var screenRect = new Rect(
                Math.Round(docRect.X - _scrollX),
                Math.Round(docRect.Y - _scrollY),
                Math.Round(docRect.Width),
                Math.Round(docRect.Height));

            // Layered drop shadow matching the slate-navy aesthetic
            context.FillRectangle(PageShadowOuterBrush, screenRect.Translate(new Vector(0, 4)).Inflate(4));
            context.FillRectangle(PageShadowInnerBrush, screenRect.Translate(new Vector(0, 2)).Inflate(1));

            // White page paper
            context.FillRectangle(PagePaperBrush, screenRect);

            if (_pageCache.TryGetValue(i, out var cached))
            {
                cached.LastUsedTick = ++_tickCounter;
                var srcRect = new Rect(0, 0, cached.Bitmap.PixelSize.Width, cached.Bitmap.PixelSize.Height);
                context.DrawImage(cached.Bitmap, srcRect, screenRect);
            }

            context.DrawRectangle(PageBorderPen, screenRect);

            DrawCommentPins(context, tab, i, screenRect);
            DrawImageOverlays(context, tab, i, screenRect);

            if (ActiveTool == EditorTool.EditText)
            {
                DrawTextBlockOverlays(context, tab, i, screenRect);
            }

            if (_isDraggingToolBox && _dragPageIndex == i)
            {
                DrawActiveDragPreview(context);
            }
        }
    }

    private void DrawActiveDragPreview(DrawingContext context)
    {
        double minX = Math.Min(_dragStartScreen.X, _dragCurrentScreen.X);
        double minY = Math.Min(_dragStartScreen.Y, _dragCurrentScreen.Y);
        double w = Math.Abs(_dragCurrentScreen.X - _dragStartScreen.X);
        double h = Math.Abs(_dragCurrentScreen.Y - _dragStartScreen.Y);
        var box = new Rect(minX, minY, Math.Max(2, w), Math.Max(2, h));

        var (fill, pen) = ActiveTool switch
        {
            EditorTool.Highlight => (HighlightPreviewFill, HighlightPreviewPen),
            EditorTool.Redact => (RedactPreviewFill, RedactPreviewPen),
            _ => (ImagePreviewFill, ImagePreviewPen)
        };

        context.FillRectangle(fill, box);
        context.DrawRectangle(pen, box);
    }

    private void DrawCommentPins(DrawingContext context, DocumentTabViewModel tab, int pageIndex, Rect pageScreenRect)
    {
        var annotations = tab.GetCachedAnnotations(pageIndex);
        if (annotations.Count == 0)
        {
            return;
        }

        double scale = tab.ZoomFactor * PointsToDips;
        foreach (var annot in annotations)
        {
            if (annot.Kind != PdfAnnotationKind.Comment)
            {
                continue;
            }

            double x = pageScreenRect.X + (annot.Bounds.Left * scale);
            double y = pageScreenRect.Y + (annot.Bounds.Top * scale);
            double size = Math.Clamp(16 * tab.ZoomFactor, 12, 26);
            var pinRect = new Rect(x, y, size, size);
            context.FillRectangle(CommentPinFill, pinRect, (float)(size * 0.2));
            context.DrawRectangle(CommentPinPen, pinRect, (float)(size * 0.2));
        }
    }

    private void DrawImageOverlays(DrawingContext context, DocumentTabViewModel tab, int pageIndex, Rect pageScreenRect)
    {
        if (ActiveTool is not (EditorTool.Select or EditorTool.EditText))
        {
            return;
        }

        double scale = tab.ZoomFactor * PointsToDips;

        if (_hoveredImage is { } hovered && hovered.PageIndex == pageIndex &&
            tab.SelectedImage?.ObjectIndex != hovered.ObjectIndex)
        {
            var hoverBox = ToScreenImageRect(hovered.Bounds, pageScreenRect, scale);
            context.DrawRectangle(BlockHoverPen, hoverBox);
        }

        if (_imageDragMode != ImageDragMode.None && _draggingImage is { } dragging && dragging.PageIndex == pageIndex)
        {
            var dragBox = ToScreenImageRect(_imageDragCurrentBounds, pageScreenRect, scale);
            context.FillRectangle(ImagePreviewFill, dragBox);
            context.DrawRectangle(BlockSelectPen, dragBox);
            DrawSelectionHandles(context, dragBox);
            return;
        }

        if (tab.SelectedImage is { } selected && selected.PageIndex == pageIndex)
        {
            var box = ToScreenImageRect(selected.Bounds, pageScreenRect, scale);
            context.DrawRectangle(BlockSelectPen, box);
            DrawSelectionHandles(context, box);
        }
    }

    private bool TryBeginImageInteraction(Point screenPos, IPointer pointer)
    {
        var tab = DocumentTab;
        if (tab is null || tab.PageCount == 0)
        {
            return false;
        }

        if (tab.SelectedImage is { } sel &&
            TryGetPageScreenRect(sel.PageIndex, out var selPageRect))
        {
            double scale = tab.ZoomFactor * PointsToDips;
            var selBox = ToScreenImageRect(sel.Bounds, selPageRect, scale);
            var handleMode = HitTestImageHandle(selBox, screenPos);
            if (handleMode != ImageDragMode.None)
            {
                BeginImageDrag(sel, handleMode, screenPos, pointer);
                return true;
            }
        }

        // In EditText mode, prefer editing text blocks when clicking directly over a text block
        if (ActiveTool == EditorTool.EditText && HitTestTextBlock(screenPos, out _) is not null)
        {
            return false;
        }

        if (TryHitTestImage(screenPos, out var hitImage, out _))
        {
            tab.SelectedTextBlock = null;
            tab.SelectedImage = hitImage;
            tab.CurrentPageIndex = hitImage.PageIndex;
            BeginImageDrag(hitImage, ImageDragMode.Move, screenPos, pointer);
            return true;
        }

        return false;
    }

    private void BeginImageDrag(PdfImageObjectInfo image, ImageDragMode mode, Point startScreen, IPointer pointer)
    {
        _imageDragMode = mode;
        _draggingImage = image;
        _imageDragStartScreen = startScreen;
        _imageDragOriginalBounds = image.Bounds;
        _imageDragCurrentBounds = image.Bounds;
        Cursor = CursorForImageDragMode(mode);
        pointer.Capture(this);
        InvalidateVisual();
    }

    private void UpdateImageDragBounds(Point currentScreen)
    {
        if (DocumentTab is null || _draggingImage is null)
        {
            return;
        }

        double scale = DocumentTab.ZoomFactor * PointsToDips;
        double dxPts = (currentScreen.X - _imageDragStartScreen.X) / scale;
        double dyPts = (currentScreen.Y - _imageDragStartScreen.Y) / scale;

        var orig = _imageDragOriginalBounds;
        double left = orig.Left;
        double top = orig.Top;
        double right = orig.Right;
        double bottom = orig.Bottom;
        const double minSizePts = 12.0;

        switch (_imageDragMode)
        {
            case ImageDragMode.Move:
                left += dxPts;
                right += dxPts;
                top += dyPts;
                bottom += dyPts;
                break;
            case ImageDragMode.ResizeNW:
                left = Math.Min(right - minSizePts, orig.Left + dxPts);
                top = Math.Min(bottom - minSizePts, orig.Top + dyPts);
                break;
            case ImageDragMode.ResizeN:
                top = Math.Min(bottom - minSizePts, orig.Top + dyPts);
                break;
            case ImageDragMode.ResizeNE:
                right = Math.Max(left + minSizePts, orig.Right + dxPts);
                top = Math.Min(bottom - minSizePts, orig.Top + dyPts);
                break;
            case ImageDragMode.ResizeE:
                right = Math.Max(left + minSizePts, orig.Right + dxPts);
                break;
            case ImageDragMode.ResizeSE:
                right = Math.Max(left + minSizePts, orig.Right + dxPts);
                bottom = Math.Max(top + minSizePts, orig.Bottom + dyPts);
                break;
            case ImageDragMode.ResizeS:
                bottom = Math.Max(top + minSizePts, orig.Bottom + dyPts);
                break;
            case ImageDragMode.ResizeSW:
                left = Math.Min(right - minSizePts, orig.Left + dxPts);
                bottom = Math.Max(top + minSizePts, orig.Bottom + dyPts);
                break;
            case ImageDragMode.ResizeW:
                left = Math.Min(right - minSizePts, orig.Left + dxPts);
                break;
        }

        _imageDragCurrentBounds = new PdfRect(left, top, right, bottom);
    }

    private void UpdateSelectHoverCursor(Point screenPos)
    {
        var tab = DocumentTab;
        if (tab is null)
        {
            Cursor = Cursor.Default;
            return;
        }

        if (tab.SelectedImage is { } sel &&
            TryGetPageScreenRect(sel.PageIndex, out var selPageRect))
        {
            double scale = tab.ZoomFactor * PointsToDips;
            var selBox = ToScreenImageRect(sel.Bounds, selPageRect, scale);
            var handle = HitTestImageHandle(selBox, screenPos);
            if (handle != ImageDragMode.None)
            {
                Cursor = CursorForImageDragMode(handle);
                return;
            }
        }

        TryHitTestImage(screenPos, out var hit, out _);
        if (!Equals(_hoveredImage, hit))
        {
            _hoveredImage = hit;
            InvalidateVisual();
        }

        Cursor = hit is not null ? new Cursor(StandardCursorType.SizeAll) : Cursor.Default;
    }

    private bool TryHitTestImage(Point screenPos, out PdfImageObjectInfo image, out Rect imageScreenRect)
    {
        image = null!;
        imageScreenRect = default;
        var tab = DocumentTab;
        if (tab is null || !TryHitTestPage(screenPos, out int pageIndex, out var pageScreenRect))
        {
            return false;
        }

        double scale = tab.ZoomFactor * PointsToDips;
        var images = tab.GetCachedImages(pageIndex);
        for (int i = images.Count - 1; i >= 0; i--)
        {
            var img = images[i];
            var box = ToScreenImageRect(img.Bounds, pageScreenRect, scale);
            if (box.Contains(screenPos))
            {
                image = img;
                imageScreenRect = box;
                return true;
            }
        }

        return false;
    }

    private bool TryGetPageScreenRect(int pageIndex, out Rect pageScreenRect)
    {
        pageScreenRect = default;
        var tab = DocumentTab;
        if (tab is null || pageIndex < 0 || pageIndex >= tab.PageCount)
        {
            return false;
        }

        var rects = ComputePageRects(tab, out _, out _);
        if (pageIndex >= rects.Count)
        {
            return false;
        }

        var docRect = rects[pageIndex];
        pageScreenRect = new Rect(
            Math.Round(docRect.X - _scrollX),
            Math.Round(docRect.Y - _scrollY),
            Math.Round(docRect.Width),
            Math.Round(docRect.Height));
        return true;
    }

    private static Rect ToScreenImageRect(PdfRect bounds, Rect pageScreenRect, double scale) =>
        new(
            pageScreenRect.X + (bounds.Left * scale),
            pageScreenRect.Y + (bounds.Top * scale),
            Math.Max(8, bounds.Width * scale),
            Math.Max(8, bounds.Height * scale));

    private static ImageDragMode HitTestImageHandle(Rect box, Point screenPos)
    {
        const double hitRadius = 6.0;
        double midX = box.X + (box.Width * 0.5);
        double midY = box.Y + (box.Height * 0.5);

        (ImageDragMode Mode, Point Center)[] handles =
        [
            (ImageDragMode.ResizeNW, new Point(box.Left, box.Top)),
            (ImageDragMode.ResizeN, new Point(midX, box.Top)),
            (ImageDragMode.ResizeNE, new Point(box.Right, box.Top)),
            (ImageDragMode.ResizeE, new Point(box.Right, midY)),
            (ImageDragMode.ResizeSE, new Point(box.Right, box.Bottom)),
            (ImageDragMode.ResizeS, new Point(midX, box.Bottom)),
            (ImageDragMode.ResizeSW, new Point(box.Left, box.Bottom)),
            (ImageDragMode.ResizeW, new Point(box.Left, midY))
        ];

        foreach (var (mode, center) in handles)
        {
            var hitRect = new Rect(center.X - hitRadius, center.Y - hitRadius, hitRadius * 2, hitRadius * 2);
            if (hitRect.Contains(screenPos))
            {
                return mode;
            }
        }

        return ImageDragMode.None;
    }

    private static Cursor CursorForImageDragMode(ImageDragMode mode) => mode switch
    {
        ImageDragMode.Move => new Cursor(StandardCursorType.SizeAll),
        ImageDragMode.ResizeNW or ImageDragMode.ResizeSE => new Cursor(StandardCursorType.TopLeftCorner),
        ImageDragMode.ResizeNE or ImageDragMode.ResizeSW => new Cursor(StandardCursorType.TopRightCorner),
        ImageDragMode.ResizeN or ImageDragMode.ResizeS => new Cursor(StandardCursorType.SizeNorthSouth),
        ImageDragMode.ResizeE or ImageDragMode.ResizeW => new Cursor(StandardCursorType.SizeWestEast),
        _ => Cursor.Default
    };

    private bool TryHitTestPage(Point screenPos, out int pageIndex, out Rect pageScreenRect)
    {
        pageIndex = -1;
        pageScreenRect = default;
        var tab = DocumentTab;
        if (tab is null || tab.PageCount == 0)
        {
            return false;
        }

        var rects = ComputePageRects(tab, out _, out _);
        for (int i = 0; i < rects.Count; i++)
        {
            var docRect = rects[i];
            var screenRect = new Rect(
                Math.Round(docRect.X - _scrollX),
                Math.Round(docRect.Y - _scrollY),
                Math.Round(docRect.Width),
                Math.Round(docRect.Height));

            if (screenRect.Contains(screenPos))
            {
                pageIndex = i;
                pageScreenRect = screenRect;
                return true;
            }
        }

        return false;
    }

    private bool TryHitTestCommentPin(Point screenPos, out int pageIndex, out PdfAnnotationInfo annotation)
    {
        pageIndex = -1;
        annotation = null!;
        var tab = DocumentTab;
        if (tab is null || tab.PageCount == 0)
        {
            return false;
        }

        if (!TryHitTestPage(screenPos, out int pIdx, out var pageScreenRect))
        {
            return false;
        }

        double scale = tab.ZoomFactor * PointsToDips;
        double size = Math.Clamp(16 * tab.ZoomFactor, 12, 26);
        foreach (var annot in tab.GetCachedAnnotations(pIdx))
        {
            if (annot.Kind != PdfAnnotationKind.Comment)
            {
                continue;
            }

            var pinRect = new Rect(
                pageScreenRect.X + (annot.Bounds.Left * scale),
                pageScreenRect.Y + (annot.Bounds.Top * scale),
                size,
                size);
            if (pinRect.Inflate(4).Contains(screenPos))
            {
                pageIndex = pIdx;
                annotation = annot;
                return true;
            }
        }

        return false;
    }

    private static Point ClampPointToRect(Point p, Rect rect) =>
        new(Math.Clamp(p.X, rect.Left, rect.Right), Math.Clamp(p.Y, rect.Top, rect.Bottom));

    private void DrawTextBlockOverlays(DrawingContext context, DocumentTabViewModel tab, int pageIndex, Rect pageScreenRect)
    {
        var layout = tab.GetCachedLayout(pageIndex);
        if (layout is null || layout.Blocks.Count == 0)
        {
            return;
        }

        double scale = tab.ZoomFactor * PointsToDips;
        string? hoveredId = tab.HoveredTextBlock?.Id;
        string? selectedId = tab.SelectedTextBlock?.Id;

        foreach (var block in layout.Blocks)
        {
            var box = ToScreenBlockRect(block.Bounds, pageScreenRect, scale);
            bool isSelected = block.Id == selectedId;
            bool isHovered = block.Id == hoveredId;

            if (isSelected)
            {
                continue; // Draw selected block last so handles sit on top
            }

            if (isHovered)
            {
                context.FillRectangle(BlockHoverFillBrush, box);
                context.DrawRectangle(BlockHoverPen, box);
            }
            else
            {
                context.DrawRectangle(BlockGuidePen, box);
            }
        }

        if (tab.SelectedTextBlock is { } selected && selected.PageIndex == pageIndex)
        {
            var box = ToScreenBlockRect(selected.Bounds, pageScreenRect, scale);
            if (EditingBlock is not null && EditingBlock.Id == selected.Id)
            {
                var maskColor = SamplePageBackgroundColor(pageIndex, pageScreenRect, selected.Bounds, scale);
                context.FillRectangle(new SolidColorBrush(maskColor), box);
            }
            else
            {
                context.FillRectangle(BlockSelectFillBrush, box);
            }

            context.DrawRectangle(BlockSelectPen, box);
            DrawSelectionHandles(context, box);
        }
    }

    private unsafe Color SamplePageBackgroundColor(int pageIndex, Rect pageScreenRect, PdfRect blockBoundsPts, double scale)
    {
        if (!_pageCache.TryGetValue(pageIndex, out var cached))
        {
            return Colors.White;
        }

        var bmp = cached.Bitmap;
        int w = bmp.PixelSize.Width;
        int h = bmp.PixelSize.Height;
        if (w <= 0 || h <= 0)
        {
            return Colors.White;
        }

        double relX = Math.Clamp((blockBoundsPts.Left - 2.0) * scale / Math.Max(1.0, pageScreenRect.Width), 0.0, 0.99);
        double relY = Math.Clamp((blockBoundsPts.Top - 2.0) * scale / Math.Max(1.0, pageScreenRect.Height), 0.0, 0.99);
        int px = Math.Clamp((int)(relX * w), 0, w - 1);
        int py = Math.Clamp((int)(relY * h), 0, h - 1);

        using var fb = bmp.Lock();
        byte* row = (byte*)fb.Address.ToPointer() + (py * fb.RowBytes);
        byte b = row[(px * 4) + 0];
        byte g = row[(px * 4) + 1];
        byte r = row[(px * 4) + 2];
        return Color.FromRgb(r, g, b);
    }

    private static Rect ToScreenBlockRect(PdfRect bounds, Rect pageScreenRect, double scale)
    {
        const double pad = 3.0;
        return new Rect(
            pageScreenRect.X + (bounds.Left * scale) - pad,
            pageScreenRect.Y + (bounds.Top * scale) - pad,
            Math.Max(8, (bounds.Width * scale) + (pad * 2)),
            Math.Max(8, (bounds.Height * scale) + (pad * 2)));
    }

    private static void DrawSelectionHandles(DrawingContext context, Rect box)
    {
        const double hs = 6.0;
        double half = hs * 0.5;
        double midX = box.X + (box.Width * 0.5);
        double midY = box.Y + (box.Height * 0.5);

        ReadOnlySpan<Point> centers =
        [
            new Point(box.Left, box.Top),
            new Point(midX, box.Top),
            new Point(box.Right, box.Top),
            new Point(box.Right, midY),
            new Point(box.Right, box.Bottom),
            new Point(midX, box.Bottom),
            new Point(box.Left, box.Bottom),
            new Point(box.Left, midY)
        ];

        foreach (var c in centers)
        {
            var handleRect = new Rect(c.X - half, c.Y - half, hs, hs);
            context.FillRectangle(HandleFillBrush, handleRect);
            context.DrawRectangle(HandleBorderPen, handleRect);
        }
    }

    private PdfTextBlock? HitTestTextBlock(Point screenPos, out int hitPageIndex)
    {
        hitPageIndex = -1;
        var tab = DocumentTab;
        if (tab is null || tab.PageCount == 0)
        {
            return null;
        }

        var rects = ComputePageRects(tab, out _, out _);
        double scale = tab.ZoomFactor * PointsToDips;

        for (int i = 0; i < rects.Count; i++)
        {
            var docRect = rects[i];
            var screenRect = new Rect(
                Math.Round(docRect.X - _scrollX),
                Math.Round(docRect.Y - _scrollY),
                Math.Round(docRect.Width),
                Math.Round(docRect.Height));

            if (!screenRect.Contains(screenPos))
            {
                continue;
            }

            var layout = tab.GetCachedLayout(i);
            if (layout is null)
            {
                return null;
            }

            double ptX = (screenPos.X - screenRect.X) / scale;
            double ptY = (screenPos.Y - screenRect.Y) / scale;

            PdfTextBlock? best = null;
            double bestArea = double.MaxValue;
            foreach (var block in layout.Blocks)
            {
                if (block.Bounds.Contains(ptX, ptY, padding: 4.0))
                {
                    double area = block.Bounds.Width * block.Bounds.Height;
                    if (area < bestArea)
                    {
                        bestArea = area;
                        best = block;
                    }
                }
            }

            if (best is not null)
            {
                hitPageIndex = i;
                return best;
            }

            return null;
        }

        return null;
    }

    private void ScrollToPage(int pageIndex, bool updateTabIndex)
    {
        var tab = DocumentTab;
        if (tab is null || tab.PageCount == 0)
        {
            return;
        }

        int clamped = Math.Clamp(pageIndex, 0, tab.PageCount - 1);
        var rects = ComputePageRects(tab, out _, out _);
        if (clamped < rects.Count)
        {
            _scrollY = Math.Max(0, rects[clamped].Top - OuterPadding);
            ClampScrollOffsets();
            if (updateTabIndex)
            {
                _suppressPageSync = true;
                tab.CurrentPageIndex = clamped;
                _suppressPageSync = false;
            }

            ViewportTransformChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            TriggerVisiblePageRenders(forceRefresh: false);
        }
    }

    private void SyncCurrentPageFromScroll()
    {
        if (_suppressPageSync || DocumentTab is null || DocumentTab.PageCount == 0)
        {
            return;
        }

        var rects = ComputePageRects(DocumentTab, out _, out _);
        double viewportCenterY = _scrollY + (Bounds.Height * 0.4);

        int bestIndex = 0;
        double bestDist = double.MaxValue;
        for (int i = 0; i < rects.Count; i++)
        {
            var r = rects[i];
            if (viewportCenterY >= r.Top && viewportCenterY <= r.Bottom)
            {
                bestIndex = i;
                break;
            }

            double center = (r.Top + r.Bottom) * 0.5;
            double dist = Math.Abs(viewportCenterY - center);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestIndex = i;
            }
        }

        if (DocumentTab.CurrentPageIndex != bestIndex)
        {
            DocumentTab.CurrentPageIndex = bestIndex;
        }
    }

    private IReadOnlyList<Rect> ComputePageRects(DocumentTabViewModel tab, out double totalWidth, out double totalHeight)
    {
        double zoom = tab.ZoomFactor;
        double viewportWidth = Bounds.Width;

        if (ReferenceEquals(_cachedRectsTab, tab) &&
            Math.Abs(_cachedRectsZoom - zoom) < 0.00001 &&
            Math.Abs(_cachedRectsViewportWidth - viewportWidth) < 0.5 &&
            _cachedPageRects.Count == tab.Session.Pages.Count)
        {
            totalWidth = _cachedTotalWidth;
            totalHeight = _cachedTotalHeight;
            return _cachedPageRects;
        }

        var pages = tab.Session.Pages;
        _cachedPageRects.Clear();
        if (_cachedPageRects.Capacity < pages.Count)
        {
            _cachedPageRects.Capacity = pages.Count;
        }

        double maxPageWidth = 0;
        foreach (var p in pages)
        {
            maxPageWidth = Math.Max(maxPageWidth, p.WidthPoints * PointsToDips * zoom);
        }

        totalWidth = Math.Max(viewportWidth, maxPageWidth + (OuterPadding * 2));
        double y = OuterPadding;

        foreach (var p in pages)
        {
            double w = p.WidthPoints * PointsToDips * zoom;
            double h = p.HeightPoints * PointsToDips * zoom;
            double x = Math.Max(OuterPadding, (totalWidth - w) * 0.5);
            _cachedPageRects.Add(new Rect(x, y, w, h));
            y += h + PageGap;
        }

        totalHeight = pages.Count > 0 ? y - PageGap + OuterPadding : Bounds.Height;

        _cachedRectsTab = tab;
        _cachedRectsZoom = zoom;
        _cachedRectsViewportWidth = viewportWidth;
        _cachedTotalWidth = totalWidth;
        _cachedTotalHeight = totalHeight;
        return _cachedPageRects;
    }

    private void ClampScrollOffsets()
    {
        if (DocumentTab is null || DocumentTab.PageCount == 0)
        {
            _scrollX = 0;
            _scrollY = 0;
            return;
        }

        ComputePageRects(DocumentTab, out double totalWidth, out double totalHeight);
        double maxScrollX = Math.Max(0, totalWidth - Bounds.Width);
        double maxScrollY = Math.Max(0, totalHeight - Bounds.Height);

        _scrollX = Math.Clamp(_scrollX, 0, maxScrollX);
        _scrollY = Math.Clamp(_scrollY, 0, maxScrollY);
    }

    private void TriggerVisiblePageRenders(bool forceRefresh)
    {
        var tab = DocumentTab;
        if (tab is null || tab.PageCount == 0 || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        double dpiScale = this.GetVisualRoot()?.RenderScaling ?? 1.0;
        double zoom = tab.ZoomFactor;
        var rects = ComputePageRects(tab, out _, out _);

        double viewTop = _scrollY - 250;
        double viewBottom = _scrollY + Bounds.Height + 250;

        var visibleIndices = new List<int>();
        for (int i = 0; i < rects.Count; i++)
        {
            if (rects[i].Bottom >= viewTop && rects[i].Top <= viewBottom)
            {
                visibleIndices.Add(i);
            }
        }

        if (visibleIndices.Count == 0 && rects.Count > 0)
        {
            visibleIndices.Add(Math.Clamp(tab.CurrentPageIndex, 0, rects.Count - 1));
        }

        if (forceRefresh)
        {
            _renderCts?.Cancel();
            _renderCts?.Dispose();
            _renderCts = new CancellationTokenSource();
        }
        else
        {
            _renderCts ??= new CancellationTokenSource();
        }

        var token = _renderCts.Token;

        foreach (int pageIndex in visibleIndices)
        {
            if (ActiveTool == EditorTool.EditText)
            {
                _ = tab.EnsurePageLayoutAsync(pageIndex, cancellationToken: token);
            }

            if (!forceRefresh && _pageCache.TryGetValue(pageIndex, out var existing))
            {
                bool sameScale = Math.Abs(existing.RenderedZoom - zoom) < 0.015
                              && Math.Abs(existing.RenderedDpi - dpiScale) < 0.05;
                if (sameScale)
                {
                    continue;
                }
            }

            if (_inFlightPages.Contains(pageIndex))
            {
                continue;
            }

            var pageRect = rects[pageIndex];
            int pixelW = (int)Math.Round(pageRect.Width * dpiScale);
            int pixelH = (int)Math.Round(pageRect.Height * dpiScale);

            // Clamp huge zoom dimensions proportionally
            if (pixelW > MaxRenderDimensionPx || pixelH > MaxRenderDimensionPx)
            {
                double scaleDown = Math.Min(
                    (double)MaxRenderDimensionPx / Math.Max(1, pixelW),
                    (double)MaxRenderDimensionPx / Math.Max(1, pixelH));
                pixelW = Math.Max(1, (int)Math.Round(pixelW * scaleDown));
                pixelH = Math.Max(1, (int)Math.Round(pixelH * scaleDown));
            }

            _ = RenderPageIntoCacheAsync(tab, pageIndex, pixelW, pixelH, zoom, dpiScale, token);
        }
    }

    private async Task RenderPageIntoCacheAsync(
        DocumentTabViewModel tab,
        int pageIndex,
        int pixelW,
        int pixelH,
        double targetZoom,
        double targetDpi,
        CancellationToken token)
    {
        _inFlightPages.Add(pageIndex);
        try
        {
            using var skBitmap = await tab.Renderer.RenderPageAsync(
                tab.Session,
                pageIndex,
                pixelW,
                pixelH,
                new PdfRenderOptions(RenderAnnotations: true, OptimizeTextForLcd: true),
                token);

            if (token.IsCancellationRequested || !ReferenceEquals(DocumentTab, tab))
            {
                return;
            }

            var wb = SkiaBitmapAdapter.ToWriteableBitmap(skBitmap);

            if (_pageCache.TryGetValue(pageIndex, out var old))
            {
                old.Dispose();
            }

            _pageCache[pageIndex] = new CachedPageBitmap
            {
                Bitmap = wb,
                RenderedZoom = targetZoom,
                RenderedDpi = targetDpi,
                LastUsedTick = ++_tickCounter
            };

            EvictOldPagesIfNeeded(keepPageIndex: pageIndex);
            InvalidateVisual();
        }
        catch (OperationCanceledException)
        {
            // Superseded by newer zoom or tab change
        }
        catch (ObjectDisposedException)
        {
            // Tab closed while rendering
        }
        finally
        {
            _inFlightPages.Remove(pageIndex);
        }
    }

    private void EvictOldPagesIfNeeded(int keepPageIndex)
    {
        while (_pageCache.Count > MaxCachedPages)
        {
            int? oldestKey = null;
            long oldestTick = long.MaxValue;
            foreach (var kvp in _pageCache)
            {
                if (kvp.Key == keepPageIndex)
                {
                    continue;
                }

                if (kvp.Value.LastUsedTick < oldestTick)
                {
                    oldestTick = kvp.Value.LastUsedTick;
                    oldestKey = kvp.Key;
                }
            }

            if (oldestKey is null)
            {
                break;
            }

            _pageCache[oldestKey.Value].Dispose();
            _pageCache.Remove(oldestKey.Value);
        }
    }
}
