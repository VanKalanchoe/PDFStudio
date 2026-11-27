using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfStudio.App.Models;
using PdfStudio.App.Services;
using PdfStudio.Core.Documents;
using PdfStudio.Core.Editing;
using PdfStudio.Core.Layout;
using SkiaSharp;

namespace PdfStudio.App.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly IPdfDocumentService _documentService;
    private readonly PdfPageRenderer _pageRenderer;
    private readonly TextBlockDetector _textBlockDetector;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveDocument))]
    [NotifyPropertyChangedFor(nameof(StatusBarText))]
    [NotifyPropertyChangedFor(nameof(ZoomPercent))]
    [NotifyPropertyChangedFor(nameof(ZoomPercentText))]
    [NotifyPropertyChangedFor(nameof(CanUndo))]
    [NotifyPropertyChangedFor(nameof(CanRedo))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private DocumentTabViewModel? _activeTab;

    [ObservableProperty]
    private bool _isEditingInline;

    [ObservableProperty]
    private PdfTextBlock? _editingBlock;

    [ObservableProperty]
    private string _inlineEditText = string.Empty;

    [ObservableProperty]
    private IBrush _inlineEditorBackgroundBrush = new SolidColorBrush(Color.FromArgb(248, 255, 255, 255));

    private PdfColor _selectedTextColor = new(17, 24, 39, 255);

    [ObservableProperty]
    private EditorTool _activeTool = EditorTool.Select;

    [ObservableProperty]
    private bool _isPagesSidebarSelected = true;

    [ObservableProperty]
    private bool _isOutlineSidebarSelected;

    [ObservableProperty]
    private bool _isFindBarOpen;

    [ObservableProperty]
    private string _statusNotification = string.Empty;

    // Right Inspector properties (wired cleanly for Milestone 2/3 binding)
    [ObservableProperty]
    private string _selectedFontFamily = "Inter";

    [ObservableProperty]
    private string _selectedFontWeight = "SemiBold";

    [ObservableProperty]
    private string _selectedFontSizeText = "11 pt";

    [ObservableProperty]
    private bool _isBoldActive;

    [ObservableProperty]
    private bool _isItalicActive;

    [ObservableProperty]
    private bool _isUnderlineActive;

    [ObservableProperty]
    private TextAlignMode _textAlignment = TextAlignMode.Left;

    [ObservableProperty]
    private string _letterSpacingText = "0.0 em";

    [ObservableProperty]
    private string _lineSpacingText = "1.45";

    [ObservableProperty]
    private string _blockXText = "72";

    [ObservableProperty]
    private string _blockYText = "248";

    [ObservableProperty]
    private string _blockWText = "468";

    [ObservableProperty]
    private string _blockHText = "86";

    [ObservableProperty]
    private bool _autoReflowParagraph;

    [ObservableProperty]
    private string _findReplaceQuery = string.Empty;

    [ObservableProperty]
    private bool _isCaseSensitiveSearch;

    [ObservableProperty]
    private string _fontSearchFilter = string.Empty;

    [ObservableProperty]
    private IBrush _selectedTextColorBrush = new SolidColorBrush(Color.FromRgb(17, 24, 39));

    [ObservableProperty]
    private string _fontStatusBadgeText = "Embedded font • Missing glyphs auto-matched";

    [ObservableProperty]
    private bool _isCommentEditorOpen;

    [ObservableProperty]
    private string _commentDraftText = string.Empty;

    [ObservableProperty]
    private double _commentPopupX;

    [ObservableProperty]
    private double _commentPopupY;

    [ObservableProperty]
    private bool _isEditingExistingComment;

    private int _commentPageIndex;
    private double _commentPtX;
    private double _commentPtY;
    private int _commentAnnotIndex = -1;

    private byte[]? _pendingImageBytes;
    private string? _pendingImageName;

    [ObservableProperty]
    private bool _isSettingsOpen;

    [ObservableProperty]
    private bool _isCheckingOrInstallingUpdate;

    [ObservableProperty]
    private bool _hasAvailableUpdate;

    [ObservableProperty]
    private string _updateStatusText = string.Empty;

    private readonly AppUpdateService _updateService = new();
    private AppUpdateCheckResult? _availableUpdate;
    private List<string> _allFontFamilies;

    public MainWindowViewModel()
        : this(new PdfDocumentService(), new PdfPageRenderer(), new TextBlockDetector())
    {
    }

    public MainWindowViewModel(
        IPdfDocumentService documentService,
        PdfPageRenderer pageRenderer,
        TextBlockDetector? textBlockDetector = null)
    {
        _documentService = documentService;
        _pageRenderer = pageRenderer;
        _textBlockDetector = textBlockDetector ?? new TextBlockDetector();

        var settings = AppSettingsService.Load();
        Loc.LanguageCode = settings.Language;

        _allFontFamilies = ["Inter", "Segoe UI", "Arial", "Helvetica", "Times New Roman", "Georgia", "Consolas"];
        FilteredFontFamilies = new ObservableCollection<string>(_allFontFamilies);
        FontWeights = new ObservableCollection<string>(["Regular", "Medium", "SemiBold", "Bold"]);
        ZoomPresets = new ObservableCollection<string>(BuildZoomPresets());
        FontStatusBadgeText = Loc.SelectBlockBadge;

        _ = LoadSystemFontFamiliesAsync();
        _ = CheckForUpdatesSilentlyAsync();
    }

    public LocalizationService Loc { get; } = new();
    public string CurrentAppVersionLabel => Loc.CurrentVersionText(AppUpdateService.CurrentVersionString);

    private string[] BuildZoomPresets() =>
        ["50%", "75%", "100%", "125%", "150%", "200%", Loc.FitWidthPreset, Loc.FitPagePreset];

    private async Task LoadSystemFontFamiliesAsync()
    {
        var loaded = await Task.Run(LoadSystemFontFamilies).ConfigureAwait(false);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            _allFontFamilies = loaded;
            if (string.IsNullOrWhiteSpace(FontSearchFilter))
            {
                FilteredFontFamilies.Clear();
                foreach (var font in _allFontFamilies)
                {
                    FilteredFontFamilies.Add(font);
                }
            }
        });
    }

    public ObservableCollection<DocumentTabViewModel> Tabs { get; } = new();
    public ObservableCollection<string> FilteredFontFamilies { get; }
    public ObservableCollection<string> FontWeights { get; }
    public ObservableCollection<string> ZoomPresets { get; }

    public bool HasActiveDocument => ActiveTab is not null;

    public bool IsSelectTool => ActiveTool == EditorTool.Select;
    public bool IsEditTextTool => ActiveTool == EditorTool.EditText;
    public bool IsAddImageTool => ActiveTool == EditorTool.AddImage;
    public bool IsHighlightTool => ActiveTool == EditorTool.Highlight;
    public bool IsCommentTool => ActiveTool == EditorTool.Comment;
    public bool IsRedactTool => ActiveTool == EditorTool.Redact;
    public bool IsOcrTool => ActiveTool == EditorTool.Ocr;

    public bool IsAlignLeft => TextAlignment == TextAlignMode.Left;
    public bool IsAlignCenter => TextAlignment == TextAlignMode.Center;
    public bool IsAlignRight => TextAlignment == TextAlignMode.Right;

    public double ZoomPercent
    {
        get => ActiveTab?.ZoomPercent ?? 100.0;
        set
        {
            if (ActiveTab is not null)
            {
                ActiveTab.ZoomPercent = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ZoomPercentText));
            }
        }
    }

    public string ZoomPercentText => ActiveTab?.ZoomPercentText ?? "100%";

    public string StatusBarText =>
        ActiveTab is not null
            ? ActiveTab.StatusSummary
            : Loc.ReadyStatusText;

    public bool CanUndo => ActiveTab?.CanUndo ?? false;
    public bool CanRedo => ActiveTab?.CanRedo ?? false;
    public bool CanSave => ActiveTab is not null && (ActiveTab.IsDirty || string.IsNullOrWhiteSpace(ActiveTab.FilePath));

    public FontFamily InlineFontFamily => new(FontFallbackResolver.MapPdfFontFamily(SelectedFontFamily));

    public FontWeight InlineFontWeight => IsBoldActive || SelectedFontWeight is "Bold"
        ? FontWeight.Bold
        : SelectedFontWeight switch
        {
            "SemiBold" => FontWeight.SemiBold,
            "Medium" => FontWeight.Medium,
            _ => FontWeight.Normal
        };

    public FontStyle InlineFontStyle => IsItalicActive ? FontStyle.Italic : FontStyle.Normal;

    public TextAlignment InlineTextAlignment => TextAlignment switch
    {
        TextAlignMode.Center => Avalonia.Media.TextAlignment.Center,
        TextAlignMode.Right => Avalonia.Media.TextAlignment.Right,
        _ => Avalonia.Media.TextAlignment.Left
    };

    public TextWrapping InlineTextWrapping => AutoReflowParagraph ? TextWrapping.Wrap : TextWrapping.NoWrap;

    public double ParsedFontSizePoints =>
        double.TryParse(
            SelectedFontSizeText.Replace("pt", "", StringComparison.OrdinalIgnoreCase).Trim(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double size)
            ? Math.Clamp(size, 4.0, 200.0)
            : 11.0;

    public double InlineFontSizePx => Math.Max(6.0, ParsedFontSizePoints * (ActiveTab?.ZoomFactor ?? 1.0) * (96.0 / 72.0));

    public double InlineLineHeightPx =>
        InlineFontSizePx * Math.Clamp(ParseDoubleOrFallback(LineSpacingText, 1.35), 0.9, 3.0);

    public event EventHandler? OpenFileDialogRequested;
    public event EventHandler? SaveFileDialogRequested;
    public event EventHandler? InlineLayoutUpdateRequested;
    public event EventHandler? OpenImageFileDialogRequested;
    public event EventHandler? MergePdfDialogRequested;

    partial void OnActiveTabChanged(DocumentTabViewModel? oldValue, DocumentTabViewModel? newValue)
    {
        if (IsEditingInline)
        {
            CancelInlineEdit();
        }

        IsCommentEditorOpen = false;

        if (oldValue is not null)
        {
            oldValue.IsActive = false;
            oldValue.PropertyChanged -= OnActiveTabPropertyChanged;
        }

        if (newValue is not null)
        {
            newValue.IsActive = true;
            newValue.PropertyChanged += OnActiveTabPropertyChanged;
            SyncInspectorFromBlock(newValue.SelectedTextBlock);

            if (ActiveTool == EditorTool.EditText && newValue.PageCount > 0)
            {
                _ = newValue.EnsurePageLayoutAsync(newValue.CurrentPageIndex);
            }
        }
        else
        {
            SyncInspectorFromBlock(null);
        }

        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(InlineFontSizePx));
    }

    private void OnActiveTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentTabViewModel.StatusSummary))
        {
            OnPropertyChanged(nameof(StatusBarText));
        }
        else if (e.PropertyName is nameof(DocumentTabViewModel.ZoomFactor) or nameof(DocumentTabViewModel.ZoomPercent))
        {
            OnPropertyChanged(nameof(ZoomPercent));
            OnPropertyChanged(nameof(ZoomPercentText));
            OnPropertyChanged(nameof(InlineFontSizePx));
            InlineLayoutUpdateRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (e.PropertyName is nameof(DocumentTabViewModel.SelectedTextBlock))
        {
            SyncInspectorFromBlock(ActiveTab?.SelectedTextBlock);
        }
        else if (e.PropertyName is nameof(DocumentTabViewModel.CanUndo))
        {
            OnPropertyChanged(nameof(CanUndo));
        }
        else if (e.PropertyName is nameof(DocumentTabViewModel.CanRedo))
        {
            OnPropertyChanged(nameof(CanRedo));
        }
        else if (e.PropertyName is nameof(DocumentTabViewModel.IsDirty) or nameof(DocumentTabViewModel.FilePath))
        {
            OnPropertyChanged(nameof(CanSave));
        }
    }

    private void SyncInspectorFromBlock(PdfTextBlock? block)
    {
        if (IsEditingInline && EditingBlock is not null && block?.Id != EditingBlock.Id)
        {
            return;
        }

        if (block is null)
        {
            FontStatusBadgeText = Loc.SelectBlockBadge;
            return;
        }

        if (!_allFontFamilies.Contains(block.PrimaryFontFamily, StringComparer.OrdinalIgnoreCase))
        {
            _allFontFamilies.Insert(0, block.PrimaryFontFamily);
            if (string.IsNullOrWhiteSpace(FontSearchFilter) &&
                !FilteredFontFamilies.Contains(block.PrimaryFontFamily, StringComparer.OrdinalIgnoreCase))
            {
                FilteredFontFamilies.Insert(0, block.PrimaryFontFamily);
            }
        }

        var visibleColor = block.PrimaryColor.A < 16
            ? (block.PrimaryColor.R > 235 && block.PrimaryColor.G > 235 && block.PrimaryColor.B > 235
                ? new PdfColor(0, 0, 0, 255)
                : block.PrimaryColor with { A = 255 })
            : block.PrimaryColor;

        _selectedTextColor = visibleColor;
        SelectedFontFamily = block.PrimaryFontFamily;
        SelectedFontWeight = block.FontWeightLabel;
        SelectedFontSizeText = string.Format(CultureInfo.InvariantCulture, "{0:0.#} pt", block.FontSize);
        SelectedTextColorBrush = new SolidColorBrush(Color.FromArgb(
            visibleColor.A,
            visibleColor.R,
            visibleColor.G,
            visibleColor.B));

        double luminance = (0.299 * visibleColor.R) + (0.587 * visibleColor.G) + (0.114 * visibleColor.B);
        InlineEditorBackgroundBrush = luminance > 175
            ? new SolidColorBrush(Color.FromArgb(238, 28, 35, 51))
            : new SolidColorBrush(Color.FromArgb(250, 255, 255, 255));

        IsBoldActive = block.IsBold;
        IsItalicActive = block.IsItalic;
        TextAlignment = block.Alignment switch
        {
            PdfTextAlignment.Center => TextAlignMode.Center,
            PdfTextAlignment.Right => TextAlignMode.Right,
            _ => TextAlignMode.Left
        };

        LetterSpacingText = string.Format(CultureInfo.InvariantCulture, "{0:0.0} em", block.LetterSpacingEm);
        LineSpacingText = string.Format(CultureInfo.InvariantCulture, "{0:0.00}", block.LineSpacingRatio);
        FontStatusBadgeText = block.IsEmbeddedFont
            ? "Embedded font • Missing glyphs auto-matched"
            : "Standard PDF font • System match ready";

        BlockXText = string.Format(CultureInfo.InvariantCulture, "{0:0.##}", block.Bounds.Left);
        BlockYText = string.Format(CultureInfo.InvariantCulture, "{0:0.##}", block.Bounds.Top);
        BlockWText = string.Format(CultureInfo.InvariantCulture, "{0:0.##}", block.Bounds.Width);
        BlockHText = string.Format(CultureInfo.InvariantCulture, "{0:0.##}", block.Bounds.Height);
        InlineLayoutUpdateRequested?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSelectedFontFamilyChanged(string value) => OnPropertyChanged(nameof(InlineFontFamily));
    partial void OnSelectedFontWeightChanged(string value) => OnPropertyChanged(nameof(InlineFontWeight));
    partial void OnIsBoldActiveChanged(bool value) => OnPropertyChanged(nameof(InlineFontWeight));
    partial void OnIsItalicActiveChanged(bool value) => OnPropertyChanged(nameof(InlineFontStyle));
    partial void OnSelectedFontSizeTextChanged(string value)
    {
        OnPropertyChanged(nameof(InlineFontSizePx));
        OnPropertyChanged(nameof(InlineLineHeightPx));
    }

    partial void OnLineSpacingTextChanged(string value) => OnPropertyChanged(nameof(InlineLineHeightPx));
    partial void OnAutoReflowParagraphChanged(bool value) => OnPropertyChanged(nameof(InlineTextWrapping));

    partial void OnActiveToolChanged(EditorTool value)
    {
        OnPropertyChanged(nameof(IsSelectTool));
        OnPropertyChanged(nameof(IsEditTextTool));
        OnPropertyChanged(nameof(IsAddImageTool));
        OnPropertyChanged(nameof(IsHighlightTool));
        OnPropertyChanged(nameof(IsCommentTool));
        OnPropertyChanged(nameof(IsRedactTool));
        OnPropertyChanged(nameof(IsOcrTool));
    }

    partial void OnTextAlignmentChanged(TextAlignMode value)
    {
        OnPropertyChanged(nameof(IsAlignLeft));
        OnPropertyChanged(nameof(IsAlignCenter));
        OnPropertyChanged(nameof(IsAlignRight));
        OnPropertyChanged(nameof(InlineTextAlignment));
    }

    partial void OnIsPagesSidebarSelectedChanged(bool value)
    {
        if (value)
        {
            IsOutlineSidebarSelected = false;
        }
    }

    partial void OnIsOutlineSidebarSelectedChanged(bool value)
    {
        if (value)
        {
            IsPagesSidebarSelected = false;
        }
    }

    partial void OnFontSearchFilterChanged(string value)
    {
        FilteredFontFamilies.Clear();
        IEnumerable<string> matches = string.IsNullOrWhiteSpace(value)
            ? _allFontFamilies
            : _allFontFamilies.Where(f => f.Contains(value.Trim(), StringComparison.OrdinalIgnoreCase));

        foreach (var font in matches)
        {
            FilteredFontFamilies.Add(font);
        }
    }

    [RelayCommand]
    private void RequestOpenFile() => OpenFileDialogRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private async Task OpenSampleDocumentAsync()
    {
        try
        {
            var bytes = SamplePdfBuilder.CreateBusinessReportSample();
            var session = await _documentService.OpenAsync(bytes, "Annual_Report_2026.pdf");
            AttachNewTab(session);
            StatusNotification = Loc.MsgSampleLoaded;
        }
        catch (Exception ex)
        {
            StatusNotification = $"Failed to open sample PDF: {ex.Message}";
        }
    }

    public async Task OpenFilesAsync(IEnumerable<string> filePaths)
    {
        foreach (var path in filePaths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var existing = Tabs.FirstOrDefault(t =>
                string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                ActiveTab = existing;
                continue;
            }

            try
            {
                var session = await _documentService.OpenAsync(path);
                AttachNewTab(session);
                StatusNotification = Loc.MsgOpenedFile(session.Metadata.FileName);
            }
            catch (Exception ex)
            {
                StatusNotification = $"Error opening file: {ex.Message}";
            }
        }
    }

    private void AttachNewTab(PdfDocumentSession session)
    {
        var tab = new DocumentTabViewModel(session, _pageRenderer, _textBlockDetector);
        Tabs.Add(tab);
        ActiveTab = tab;
    }

    [RelayCommand]
    private void CloseTab(DocumentTabViewModel? tab)
    {
        if (tab is null)
        {
            return;
        }

        int index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        Tabs.RemoveAt(index);
        if (ReferenceEquals(ActiveTab, tab))
        {
            ActiveTab = Tabs.Count > 0 ? Tabs[Math.Clamp(index - 1, 0, Tabs.Count - 1)] : null;
        }

        tab.Dispose();
    }

    [RelayCommand]
    private async Task SelectToolAsync(EditorTool tool)
    {
        if (tool != EditorTool.EditText && IsEditingInline)
        {
            await CommitInlineEditAsync();
        }

        if (tool != EditorTool.Comment && IsCommentEditorOpen)
        {
            IsCommentEditorOpen = false;
        }

        if (tool == EditorTool.AddImage)
        {
            if (ActiveTab is null || ActiveTab.PageCount == 0)
            {
                ActiveTool = EditorTool.Select;
                StatusNotification = Loc.MsgOpenPdfFirst;
                return;
            }

            ActiveTool = EditorTool.AddImage;
            StatusNotification = Loc.MsgChooseImage;
            OpenImageFileDialogRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (tool == EditorTool.Ocr)
        {
            await RunOcrOnCurrentPageAsync();
            ActiveTool = EditorTool.Select;
            return;
        }

        ActiveTool = tool;

        if (tool == EditorTool.EditText && ActiveTab is not null && ActiveTab.PageCount > 0)
        {
            var layout = await ActiveTab.EnsurePageLayoutAsync(ActiveTab.CurrentPageIndex);
            StatusNotification = Loc.MsgEditTextHint(layout.Blocks.Count, ActiveTab.CurrentPageIndex + 1);
            return;
        }

        if (ActiveTab is not null && tool != EditorTool.EditText)
        {
            ActiveTab.HoveredTextBlock = null;
        }

        StatusNotification = tool switch
        {
            EditorTool.Highlight => Loc.MsgHighlightHint,
            EditorTool.Comment => Loc.MsgCommentHint,
            EditorTool.Redact => Loc.MsgRedactHint,
            _ => string.Empty
        };
    }

    public async Task BeginInlineEditAsync(PdfTextBlock block)
    {
        if (IsEditingInline && EditingBlock is not null && EditingBlock.Id != block.Id)
        {
            await CommitInlineEditAsync();
        }

        if (ActiveTab is not null)
        {
            ActiveTab.SelectedTextBlock = block;
        }

        SyncInspectorFromBlock(block);
        EditingBlock = block;
        InlineEditText = block.Text;
        IsEditingInline = true;
        CaptureInlineEditStartSnapshot(block.Text);
        InlineLayoutUpdateRequested?.Invoke(this, EventArgs.Empty);
    }

    private (string Text, string Font, string SizeText, bool Bold, bool Italic, TextAlignMode Align,
        string X, string Y, string W, string H, string LineSpacing, string LetterSpacing)? _inlineEditSnapshot;

    private void CaptureInlineEditStartSnapshot(string text) =>
        _inlineEditSnapshot = (
            text.Replace("\r\n", "\n").Replace('\r', '\n'),
            SelectedFontFamily,
            SelectedFontSizeText,
            IsBoldActive,
            IsItalicActive,
            TextAlignment,
            BlockXText,
            BlockYText,
            BlockWText,
            BlockHText,
            LineSpacingText,
            LetterSpacingText);

    public void CancelInlineEdit()
    {
        IsEditingInline = false;
        EditingBlock = null;
        InlineEditText = string.Empty;
        _inlineEditSnapshot = null;
    }

    public async Task CommitInlineEditAsync()
    {
        if (!IsEditingInline || EditingBlock is null || ActiveTab is null)
        {
            CancelInlineEdit();
            return;
        }

        var tab = ActiveTab;
        var originalBlock = EditingBlock;
        var startSnapshot = _inlineEditSnapshot;
        string updatedText = InlineEditText;

        IsEditingInline = false;
        EditingBlock = null;
        _inlineEditSnapshot = null;

        string normalizedUpdated = updatedText.Replace("\r\n", "\n").Replace('\r', '\n');
        string normalizedOriginal = originalBlock.Text.Replace("\r\n", "\n").Replace('\r', '\n');

        if (startSnapshot is { } snap &&
            string.Equals(normalizedUpdated, snap.Text, StringComparison.Ordinal) &&
            string.Equals(SelectedFontFamily, snap.Font, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(SelectedFontSizeText, snap.SizeText, StringComparison.Ordinal) &&
            IsBoldActive == snap.Bold &&
            IsItalicActive == snap.Italic &&
            TextAlignment == snap.Align &&
            string.Equals(BlockXText, snap.X, StringComparison.Ordinal) &&
            string.Equals(BlockYText, snap.Y, StringComparison.Ordinal) &&
            string.Equals(BlockWText, snap.W, StringComparison.Ordinal) &&
            string.Equals(BlockHText, snap.H, StringComparison.Ordinal) &&
            string.Equals(LineSpacingText, snap.LineSpacing, StringComparison.Ordinal) &&
            string.Equals(LetterSpacingText, snap.LetterSpacing, StringComparison.Ordinal))
        {
            tab.SelectedTextBlock = null;
            return;
        }

        double targetFontSize = ParsedFontSizePoints;
        var targetAlignment = TextAlignment switch
        {
            TextAlignMode.Center => PdfTextAlignment.Center,
            TextAlignMode.Right => PdfTextAlignment.Right,
            _ => PdfTextAlignment.Left
        };

        double targetX = ParseDoubleOrFallback(BlockXText, originalBlock.Bounds.Left);
        double targetY = ParseDoubleOrFallback(BlockYText, originalBlock.Bounds.Top);
        double targetW = Math.Max(4.0, ParseDoubleOrFallback(BlockWText, originalBlock.Bounds.Width));
        double targetH = Math.Max(4.0, ParseDoubleOrFallback(BlockHText, originalBlock.Bounds.Height));
        double lineSpacing = Math.Clamp(ParseDoubleOrFallback(LineSpacingText, originalBlock.LineSpacingRatio), 0.8, 3.0);
        double letterSpacing = ParseDoubleOrFallback(LetterSpacingText.Replace("em", "", StringComparison.OrdinalIgnoreCase), originalBlock.LetterSpacingEm);

        bool unchanged = string.Equals(normalizedUpdated, normalizedOriginal, StringComparison.Ordinal)
                      && string.Equals(SelectedFontFamily, originalBlock.PrimaryFontFamily, StringComparison.OrdinalIgnoreCase)
                      && Math.Abs(targetFontSize - originalBlock.FontSize) < 0.1
                      && IsBoldActive == originalBlock.IsBold
                      && IsItalicActive == originalBlock.IsItalic
                      && targetAlignment == originalBlock.Alignment
                      && Math.Abs(targetX - originalBlock.Bounds.Left) < 0.5
                      && Math.Abs(targetY - originalBlock.Bounds.Top) < 0.5
                      && Math.Abs(targetW - originalBlock.Bounds.Width) < 0.5;

        if (unchanged)
        {
            tab.SelectedTextBlock = null;
            return;
        }

        bool userEditedWidth = Math.Abs(targetW - originalBlock.Bounds.Width) >= 0.5;
        bool shouldReflow = AutoReflowParagraph && (originalBlock.Lines.Count > 1 || userEditedWidth);

        var request = new PdfTextEditRequest(
            OriginalBlock: originalBlock,
            NewText: updatedText,
            FontFamily: SelectedFontFamily,
            FontSize: targetFontSize,
            IsBold: IsBoldActive,
            IsItalic: IsItalicActive,
            FillColor: _selectedTextColor,
            Alignment: targetAlignment,
            LineSpacingRatio: lineSpacing,
            LetterSpacingEm: letterSpacing,
            TargetBounds: new PdfRect(targetX, targetY, targetX + targetW, targetY + targetH),
            AutoReflow: shouldReflow);

        var result = await tab.ApplyTextEditAsync(request);
        if (!result.Success)
        {
            StatusNotification = $"Edit failed: {result.DiagnosticMessage}";
            return;
        }

        tab.SelectedTextBlock = null;
        if (ActiveTool == EditorTool.EditText)
        {
            _ = tab.EnsurePageLayoutAsync(originalBlock.PageIndex, forceRefresh: true);
        }

        FontStatusBadgeText = result.UsedFallbackFont
            ? $"Fallback font embedded: {result.ResolvedFontFamily}"
            : $"Embedded font: {result.ResolvedFontFamily}";

        StatusNotification = result.UsedFallbackFont
            ? $"Text updated • Fallback font embedded ({result.ResolvedFontFamily})"
            : $"Text updated ({result.ResolvedFontFamily})";

        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    private static double ParseDoubleOrFallback(string text, double fallback) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double val)
            ? val
            : fallback;

    [RelayCommand]
    private void SetAlignment(TextAlignMode mode) => TextAlignment = mode;

    [RelayCommand]
    private void ZoomIn()
    {
        if (ActiveTab is not null)
        {
            ActiveTab.ZoomPercent = Math.Min(400, ActiveTab.ZoomPercent + 25);
        }
    }

    [RelayCommand]
    private void ZoomOut()
    {
        if (ActiveTab is not null)
        {
            ActiveTab.ZoomPercent = Math.Max(25, ActiveTab.ZoomPercent - 25);
        }
    }

    public void ApplyZoomPreset(string? preset)
    {
        if (ActiveTab is null || string.IsNullOrWhiteSpace(preset))
        {
            return;
        }

        if (string.Equals(preset, "Fit Width", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(preset, Loc.FitWidthPreset, StringComparison.OrdinalIgnoreCase))
        {
            ActiveTab.RequestFitWidth();
            return;
        }

        if (string.Equals(preset, "Fit Page", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(preset, Loc.FitPagePreset, StringComparison.OrdinalIgnoreCase))
        {
            ActiveTab.RequestFitPage();
            return;
        }

        if (preset.EndsWith('%') && double.TryParse(preset.TrimEnd('%'), out var pct))
        {
            ActiveTab.ZoomPercent = pct;
        }
    }

    [RelayCommand]
    private async Task SaveDocumentAsync()
    {
        if (ActiveTab is null)
        {
            return;
        }

        if (IsEditingInline)
        {
            await CommitInlineEditAsync();
        }

        if (string.IsNullOrWhiteSpace(ActiveTab.FilePath))
        {
            SaveFileDialogRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (!ActiveTab.IsDirty)
        {
            StatusNotification = Loc.MsgAlreadySaved;
            return;
        }

        await SaveToPathAsync(ActiveTab.FilePath);
    }

    [RelayCommand]
    private void ToggleSettings() => IsSettingsOpen = !IsSettingsOpen;

    [RelayCommand]
    private void CloseSettings() => IsSettingsOpen = false;

    [RelayCommand]
    private void SetLanguage(string languageCode)
    {
        Loc.LanguageCode = languageCode;
        AppSettingsService.Save(new AppSettings(Loc.LanguageCode));

        ZoomPresets.Clear();
        foreach (var preset in BuildZoomPresets())
        {
            ZoomPresets.Add(preset);
        }

        if (ActiveTab?.SelectedTextBlock is null)
        {
            FontStatusBadgeText = Loc.SelectBlockBadge;
        }

        OnPropertyChanged(nameof(StatusBarText));
        OnPropertyChanged(nameof(CurrentAppVersionLabel));
        if (_availableUpdate is not null)
        {
            UpdateStatusText = _availableUpdate.IsUpdateAvailable
                ? Loc.UpdateAvailableText(_availableUpdate.LatestVersion)
                : Loc.UpToDateText(_availableUpdate.CurrentVersion);
        }

        StatusNotification = Loc.IsGerman
            ? "Sprache auf Deutsch umgestellt."
            : "Language switched to English.";
    }

    private async Task CheckForUpdatesSilentlyAsync()
    {
        try
        {
            var result = await _updateService.CheckForUpdatesAsync().ConfigureAwait(false);
            if (!result.IsUpdateAvailable)
            {
                return;
            }

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                _availableUpdate = result;
                HasAvailableUpdate = true;
                UpdateStatusText = Loc.UpdateAvailableText(result.LatestVersion);
                if (string.IsNullOrEmpty(StatusNotification))
                {
                    StatusNotification = Loc.UpdateAvailableText(result.LatestVersion);
                }
            });
        }
        catch
        {
            // Offline or GitHub rate-limit: stay silent on background startup check
        }
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (IsCheckingOrInstallingUpdate)
        {
            return;
        }

        IsCheckingOrInstallingUpdate = true;
        UpdateStatusText = Loc.CheckingUpdatesText;
        try
        {
            var result = await _updateService.CheckForUpdatesAsync();
            _availableUpdate = result;
            HasAvailableUpdate = result.IsUpdateAvailable;
            UpdateStatusText = result.IsUpdateAvailable
                ? Loc.UpdateAvailableText(result.LatestVersion)
                : Loc.UpToDateText(result.CurrentVersion);
        }
        catch (Exception ex)
        {
            HasAvailableUpdate = false;
            UpdateStatusText = Loc.UpdateCheckFailedText(ex.Message);
        }
        finally
        {
            IsCheckingOrInstallingUpdate = false;
        }
    }

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (IsCheckingOrInstallingUpdate || _availableUpdate is not { IsUpdateAvailable: true } update)
        {
            return;
        }

        IsCheckingOrInstallingUpdate = true;
        UpdateStatusText = Loc.DownloadingUpdateText("0%");
        try
        {
            var progress = new Progress<string>(pct => UpdateStatusText = Loc.DownloadingUpdateText(pct));
            bool restarting = await _updateService.DownloadAndApplyUpdateAsync(update, progress);
            if (restarting)
            {
                UpdateStatusText = Loc.InstallingUpdateText;
                if (Avalonia.Application.Current?.ApplicationLifetime is
                    Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                {
                    desktop.Shutdown();
                }
            }
            else
            {
                UpdateStatusText = Loc.UpdateOpenedBrowserText;
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText = Loc.UpdateCheckFailedText(ex.Message);
        }
        finally
        {
            IsCheckingOrInstallingUpdate = false;
        }
    }

    [RelayCommand]
    private async Task SaveAsDocumentAsync()
    {
        if (ActiveTab is null)
        {
            return;
        }

        if (IsEditingInline)
        {
            await CommitInlineEditAsync();
        }

        SaveFileDialogRequested?.Invoke(this, EventArgs.Empty);
    }

    public async Task SaveToPathAsync(string targetPath)
    {
        if (ActiveTab is null || string.IsNullOrWhiteSpace(targetPath))
        {
            return;
        }

        try
        {
            await ActiveTab.SaveToFileAsync(targetPath);
            OnPropertyChanged(nameof(CanSave));
            StatusNotification = Loc.MsgSavedFile(ActiveTab.FileName);
        }
        catch (Exception ex)
        {
            StatusNotification = Loc.MsgSaveFailed(ex.Message);
        }
    }

    [RelayCommand]
    private async Task UndoAsync()
    {
        if (ActiveTab is null || !ActiveTab.CanUndo)
        {
            return;
        }

        CancelInlineEdit();
        if (await ActiveTab.UndoAsync())
        {
            StatusNotification = Loc.MsgUndidEdit;
            NotifyUndoRedoState();
        }
    }

    [RelayCommand]
    private async Task RedoAsync()
    {
        if (ActiveTab is null || !ActiveTab.CanRedo)
        {
            return;
        }

        CancelInlineEdit();
        if (await ActiveTab.RedoAsync())
        {
            StatusNotification = Loc.MsgRedidEdit;
            NotifyUndoRedoState();
        }
    }

    [RelayCommand]
    private void ToggleFind() => IsFindBarOpen = !IsFindBarOpen;

    [RelayCommand]
    private async Task FindNextAsync()
    {
        if (ActiveTab is null || ActiveTab.PageCount == 0)
        {
            return;
        }

        string query = FindReplaceQuery.Trim();
        if (string.IsNullOrEmpty(query))
        {
            StatusNotification = "Enter text to search.";
            return;
        }

        var comparison = IsCaseSensitiveSearch
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        int pageCount = ActiveTab.PageCount;
        int startPage = Math.Clamp(ActiveTab.CurrentPageIndex, 0, pageCount - 1);

        for (int offset = 0; offset < pageCount; offset++)
        {
            int pageIdx = (startPage + offset) % pageCount;
            var layout = await ActiveTab.EnsurePageLayoutAsync(pageIdx);
            foreach (var block in layout.Blocks)
            {
                if (block.Text.Contains(query, comparison))
                {
                    if (ActiveTab.CurrentPageIndex != pageIdx)
                    {
                        ActiveTab.CurrentPageIndex = pageIdx;
                    }

                    ActiveTab.SelectedTextBlock = block;
                    SyncInspectorFromBlock(block);
                    StatusNotification = $"Found \"{query}\" on page {pageIdx + 1}.";
                    return;
                }
            }
        }

        StatusNotification = $"No matches found for \"{query}\".";
    }

    public void SetPendingImage(byte[] imageBytes, string fileName)
    {
        _pendingImageBytes = imageBytes;
        _pendingImageName = fileName;
        ActiveTool = EditorTool.AddImage;
        StatusNotification = Loc.MsgImageReady(fileName);
    }

    public void CancelPendingImageSelection()
    {
        if (_pendingImageBytes is null)
        {
            ActiveTool = EditorTool.Select;
            StatusNotification = string.Empty;
        }
    }

    public async Task ApplyHighlightAsync(int pageIndex, PdfRect bounds)
    {
        if (ActiveTab is null)
        {
            return;
        }

        await ActiveTab.AddHighlightAsync(pageIndex, bounds);
        NotifyUndoRedoState();
        StatusNotification = $"Highlight added on page {pageIndex + 1}.";
    }

    public async Task ApplyRedactionAsync(int pageIndex, PdfRect bounds)
    {
        if (ActiveTab is null)
        {
            return;
        }

        var result = await ActiveTab.ApplyRedactionAsync(pageIndex, bounds);
        NotifyUndoRedoState();
        StatusNotification = result.Success
            ? $"Redacted {result.StrippedObjectCount} object(s) on page {pageIndex + 1}."
            : $"Redaction failed on page {pageIndex + 1}.";
    }

    public async Task PlacePendingImageAsync(int pageIndex, PdfRect bounds)
    {
        if (ActiveTab is null)
        {
            return;
        }

        if (_pendingImageBytes is not { Length: > 0 })
        {
            StatusNotification = "Choose an image file first.";
            OpenImageFileDialogRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        var placementBounds = bounds;
        if (Math.Abs(bounds.Width - 180.0) < 0.01 && Math.Abs(bounds.Height - 135.0) < 0.01)
        {
            var (pxW, pxH) = PdfImageEditor.ProbeImageDimensions(_pendingImageBytes);
            if (pxW > 0 && pxH > 0)
            {
                double aspectHeight = Math.Clamp(180.0 * pxH / pxW, 24.0, 360.0);
                placementBounds = new PdfRect(bounds.Left, bounds.Top, bounds.Left + 180.0, bounds.Top + aspectHeight);
            }
        }

        bool ok = await ActiveTab.InsertImageAsync(pageIndex, _pendingImageBytes, placementBounds);
        NotifyUndoRedoState();
        StatusNotification = ok
            ? Loc.MsgImageInserted(_pendingImageName ?? "image", pageIndex + 1)
            : "Could not decode or place the selected image.";

        if (ok)
        {
            _pendingImageBytes = null;
            _pendingImageName = null;
            ActiveTool = EditorTool.Select;
        }
    }

    public async Task MoveOrResizeImageAsync(int pageIndex, int objectIndex, PdfRect newBounds)
    {
        if (ActiveTab is null)
        {
            return;
        }

        if (await ActiveTab.SetImageBoundsAsync(pageIndex, objectIndex, newBounds))
        {
            NotifyUndoRedoState();
            StatusNotification = Loc.MsgImageUpdated(pageIndex + 1);
        }
    }

    public async Task DeleteSelectedImageAsync()
    {
        if (ActiveTab?.SelectedImage is not { } selected)
        {
            return;
        }

        if (await ActiveTab.DeleteImageAsync(selected.PageIndex, selected.ObjectIndex))
        {
            NotifyUndoRedoState();
            StatusNotification = Loc.MsgImageRemoved(selected.PageIndex + 1);
        }
    }

    public void OpenNewComment(int pageIndex, double ptX, double ptY, double screenX, double screenY)
    {
        _commentPageIndex = pageIndex;
        _commentPtX = ptX;
        _commentPtY = ptY;
        _commentAnnotIndex = -1;
        IsEditingExistingComment = false;
        CommentDraftText = string.Empty;
        CommentPopupX = Math.Max(12, screenX + 12);
        CommentPopupY = Math.Max(12, screenY - 12);
        IsCommentEditorOpen = true;
    }

    public void OpenExistingComment(int pageIndex, PdfAnnotationInfo annotation, double screenX, double screenY)
    {
        _commentPageIndex = pageIndex;
        _commentPtX = annotation.Bounds.Left;
        _commentPtY = annotation.Bounds.Top;
        _commentAnnotIndex = annotation.Index;
        IsEditingExistingComment = true;
        CommentDraftText = annotation.Contents;
        CommentPopupX = Math.Max(12, screenX + 12);
        CommentPopupY = Math.Max(12, screenY - 12);
        IsCommentEditorOpen = true;
    }

    [RelayCommand]
    private async Task SaveCommentAsync()
    {
        if (ActiveTab is null)
        {
            IsCommentEditorOpen = false;
            return;
        }

        string text = CommentDraftText.Trim();
        IsCommentEditorOpen = false;

        if (IsEditingExistingComment && _commentAnnotIndex >= 0)
        {
            await ActiveTab.DeleteAnnotationAsync(_commentPageIndex, _commentAnnotIndex);
            if (!string.IsNullOrEmpty(text))
            {
                await ActiveTab.AddCommentAsync(_commentPageIndex, _commentPtX, _commentPtY, text);
                StatusNotification = $"Comment updated on page {_commentPageIndex + 1}.";
            }
            else
            {
                StatusNotification = $"Comment deleted from page {_commentPageIndex + 1}.";
            }

            NotifyUndoRedoState();
            return;
        }

        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        await ActiveTab.AddCommentAsync(_commentPageIndex, _commentPtX, _commentPtY, text);
        NotifyUndoRedoState();
        StatusNotification = $"Comment added on page {_commentPageIndex + 1}.";
    }

    [RelayCommand]
    private async Task DeleteCommentAsync()
    {
        IsCommentEditorOpen = false;
        if (ActiveTab is null || !IsEditingExistingComment || _commentAnnotIndex < 0)
        {
            return;
        }

        await ActiveTab.DeleteAnnotationAsync(_commentPageIndex, _commentAnnotIndex);
        NotifyUndoRedoState();
        StatusNotification = $"Comment deleted from page {_commentPageIndex + 1}.";
    }

    [RelayCommand]
    private void CancelComment() => IsCommentEditorOpen = false;

    public async Task RunOcrOnCurrentPageAsync()
    {
        if (ActiveTab is null || ActiveTab.PageCount == 0)
        {
            StatusNotification = "Open a PDF document first.";
            return;
        }

        int pageIndex = ActiveTab.CurrentPageIndex;
        StatusNotification = Loc.MsgRunningOcr(pageIndex + 1);
        try
        {
            var result = await ActiveTab.RunOcrOnPageAsync(pageIndex);
            NotifyUndoRedoState();

            int charCount = result.FullText.Length;
            StatusNotification = result.Lines.Count > 0
                ? Loc.MsgOcrSuccess(pageIndex + 1, result.Lines.Count, charCount, result.MeanConfidence)
                : Loc.MsgOcrNoText(pageIndex + 1);
        }
        catch (Exception ex)
        {
            StatusNotification = $"OCR unavailable: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task MovePageUpAsync()
    {
        if (ActiveTab is null)
        {
            return;
        }

        int idx = ActiveTab.CurrentPageIndex;
        if (idx <= 0)
        {
            StatusNotification = "Page 1 is already first.";
            return;
        }

        if (await ActiveTab.MovePageAsync(idx, idx - 1))
        {
            NotifyUndoRedoState();
            StatusNotification = $"Moved page {idx + 1} to position {idx}.";
        }
    }

    [RelayCommand]
    private async Task MovePageDownAsync()
    {
        if (ActiveTab is null)
        {
            return;
        }

        int idx = ActiveTab.CurrentPageIndex;
        if (idx >= ActiveTab.PageCount - 1)
        {
            StatusNotification = "Already on the last page.";
            return;
        }

        if (await ActiveTab.MovePageAsync(idx, idx + 1))
        {
            NotifyUndoRedoState();
            StatusNotification = $"Moved page {idx + 1} to position {idx + 2}.";
        }
    }

    [RelayCommand]
    private async Task InsertBlankPageAsync()
    {
        if (ActiveTab is null)
        {
            return;
        }

        int insertAt = ActiveTab.PageCount > 0 ? ActiveTab.CurrentPageIndex + 1 : 0;
        await ActiveTab.InsertBlankPageAsync(insertAt);
        NotifyUndoRedoState();
        StatusNotification = $"Inserted blank page at position {insertAt + 1}.";
    }

    [RelayCommand]
    private async Task DeleteCurrentPageAsync()
    {
        if (ActiveTab is null)
        {
            return;
        }

        int idx = ActiveTab.CurrentPageIndex;
        if (!await ActiveTab.DeletePageAsync(idx))
        {
            StatusNotification = "Cannot delete the only remaining page.";
            return;
        }

        NotifyUndoRedoState();
        StatusNotification = $"Deleted page {idx + 1}.";
    }

    [RelayCommand]
    private void RequestMergePdf()
    {
        if (ActiveTab is null)
        {
            StatusNotification = "Open a PDF document first.";
            return;
        }

        MergePdfDialogRequested?.Invoke(this, EventArgs.Empty);
    }

    public async Task MergePdfFileAsync(string path)
    {
        if (ActiveTab is null || !File.Exists(path))
        {
            return;
        }

        byte[] otherBytes = await File.ReadAllBytesAsync(path);
        int insertAt = ActiveTab.PageCount;
        int imported = await ActiveTab.MergePdfAsync(otherBytes, insertAt);
        NotifyUndoRedoState();
        StatusNotification = imported > 0
            ? $"Merged {imported} page(s) from \"{Path.GetFileName(path)}\"."
            : $"Could not import pages from \"{Path.GetFileName(path)}\".";
    }

    private void NotifyUndoRedoState()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(StatusBarText));
    }

    [RelayCommand]
    private async Task TriggerDocumentActionAsync(string actionName)
    {
        if (string.Equals(actionName, "Detect Text Blocks", StringComparison.Ordinal))
        {
            if (ActiveTab is null || ActiveTab.PageCount == 0)
            {
                StatusNotification = Loc.MsgOpenPdfFirst;
                return;
            }

            ActiveTool = EditorTool.EditText;
            var layout = await ActiveTab.EnsurePageLayoutAsync(ActiveTab.CurrentPageIndex, forceRefresh: true);
            if (layout.Blocks.Count > 0 && ActiveTab.SelectedTextBlock is null)
            {
                ActiveTab.SelectedTextBlock = layout.Blocks[0];
            }

            StatusNotification = Loc.MsgDetectedBlocks(layout.Blocks.Count, ActiveTab.CurrentPageIndex + 1);
            return;
        }

        if (string.Equals(actionName, "Run OCR", StringComparison.Ordinal))
        {
            await RunOcrOnCurrentPageAsync();
            return;
        }

        if (string.Equals(actionName, "Rewrite / Translate Block", StringComparison.Ordinal))
        {
            if (ActiveTab is null || ActiveTab.PageCount == 0)
            {
                StatusNotification = Loc.MsgOpenPdfFirst;
                return;
            }

            ActiveTool = EditorTool.EditText;
            StatusNotification = Loc.MsgEditTextHint(
                (await ActiveTab.EnsurePageLayoutAsync(ActiveTab.CurrentPageIndex)).Blocks.Count,
                ActiveTab.CurrentPageIndex + 1);
            return;
        }

        StatusNotification = $"{actionName} ready.";
    }

    private static List<string> LoadSystemFontFamilies()
    {
        var preferred = new[] { "Inter", "Segoe UI", "Arial", "Helvetica", "Times New Roman", "Georgia", "Consolas" };
        var installed = SKFontManager.Default.FontFamilies
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new List<string>();
        foreach (var p in preferred)
        {
            if (p == "Inter" || installed.Contains(p, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(p);
            }
        }

        foreach (var f in installed)
        {
            if (!result.Contains(f, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(f);
            }
        }

        return result;
    }

    public void Dispose()
    {
        foreach (var tab in Tabs)
        {
            tab.Dispose();
        }

        Tabs.Clear();
    }
}
