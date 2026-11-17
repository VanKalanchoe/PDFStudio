using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PdfStudio.App.Models;
using PdfStudio.App.ViewModels;
using PdfStudio.Core.Editing;
using PdfStudio.Core.Layout;

namespace PdfStudio.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        PdfViewport.ViewportTransformChanged += (_, _) => RepositionInlineEditor();
        PdfViewport.BlockEditRequested += OnViewportBlockEditRequested;
        PdfViewport.CanvasBackgroundClicked += OnViewportBackgroundClicked;
        PdfViewport.HighlightRequested += OnViewportHighlightRequested;
        PdfViewport.RedactionRequested += OnViewportRedactionRequested;
        PdfViewport.ImagePlacementRequested += OnViewportImagePlacementRequested;
        PdfViewport.CommentRequested += OnViewportCommentRequested;
        PdfViewport.CommentPinClicked += OnViewportCommentPinClicked;
        PdfViewport.ImageBoundsChanged += OnViewportImageBoundsChanged;

        Opened += (_, _) => UpdateMaximizedChromePadding();
        PropertyChanged += (_, args) =>
        {
            if (args.Property == WindowStateProperty || args.Property == OffScreenMarginProperty)
            {
                UpdateMaximizedChromePadding();
            }
        };
    }

    private void UpdateMaximizedChromePadding()
    {
        if (WindowRootBorder is null)
        {
            return;
        }

        if (WindowState == WindowState.Maximized)
        {
            var m = OffScreenMargin;
            WindowRootBorder.Padding = new Thickness(
                Math.Max(m.Left, 8),
                Math.Max(m.Top, 8),
                Math.Max(m.Right, 8),
                Math.Max(m.Bottom, 8));
        }
        else
        {
            WindowRootBorder.Padding = new Thickness(0);
        }
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (ViewModel is not null)
        {
            ViewModel.OpenFileDialogRequested -= OnOpenFileDialogRequested;
            ViewModel.OpenFileDialogRequested += OnOpenFileDialogRequested;
            ViewModel.SaveFileDialogRequested -= OnSaveFileDialogRequested;
            ViewModel.SaveFileDialogRequested += OnSaveFileDialogRequested;
            ViewModel.InlineLayoutUpdateRequested -= OnInlineLayoutUpdateRequested;
            ViewModel.InlineLayoutUpdateRequested += OnInlineLayoutUpdateRequested;
            ViewModel.OpenImageFileDialogRequested -= OnOpenImageFileDialogRequested;
            ViewModel.OpenImageFileDialogRequested += OnOpenImageFileDialogRequested;
            ViewModel.MergePdfDialogRequested -= OnMergePdfDialogRequested;
            ViewModel.MergePdfDialogRequested += OnMergePdfDialogRequested;
        }
    }

    private void OnInlineLayoutUpdateRequested(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(RepositionInlineEditor);

    private async void OnViewportBlockEditRequested(object? sender, PdfTextBlock block)
    {
        if (ViewModel is null)
        {
            return;
        }

        await ViewModel.BeginInlineEditAsync(block);
        RepositionInlineEditor();

        Dispatcher.UIThread.Post(() =>
        {
            InlineTextEditor.Focus();
            InlineTextEditor.CaretIndex = InlineTextEditor.Text?.Length ?? 0;
        }, DispatcherPriority.Input);
    }

    private async void OnViewportBackgroundClicked(object? sender, EventArgs e)
    {
        if (ViewModel is not null && ViewModel.IsEditingInline)
        {
            await ViewModel.CommitInlineEditAsync();
        }
    }

    private async void OnViewportHighlightRequested(object? sender, (int PageIndex, PdfRect Bounds) e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.ApplyHighlightAsync(e.PageIndex, e.Bounds);
        }
    }

    private async void OnViewportRedactionRequested(object? sender, (int PageIndex, PdfRect Bounds) e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.ApplyRedactionAsync(e.PageIndex, e.Bounds);
        }
    }

    private async void OnViewportImagePlacementRequested(object? sender, (int PageIndex, PdfRect Bounds) e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.PlacePendingImageAsync(e.PageIndex, e.Bounds);
        }
    }

    private async void OnViewportImageBoundsChanged(object? sender, (int PageIndex, int ObjectIndex, PdfRect NewBounds) e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.MoveOrResizeImageAsync(e.PageIndex, e.ObjectIndex, e.NewBounds);
        }
    }

    private void OnViewportCommentRequested(object? sender, (int PageIndex, double PtX, double PtY, Point ScreenPos) e)
    {
        if (ViewModel is null)
        {
            return;
        }

        ViewModel.OpenNewComment(e.PageIndex, e.PtX, e.PtY, e.ScreenPos.X, e.ScreenPos.Y);
        Dispatcher.UIThread.Post(() => CommentEditorBox.Focus(), DispatcherPriority.Input);
    }

    private void OnViewportCommentPinClicked(object? sender, (int PageIndex, PdfAnnotationInfo Annotation, Point ScreenPos) e)
    {
        if (ViewModel is null)
        {
            return;
        }

        ViewModel.OpenExistingComment(e.PageIndex, e.Annotation, e.ScreenPos.X, e.ScreenPos.Y);
        Dispatcher.UIThread.Post(() =>
        {
            CommentEditorBox.Focus();
            CommentEditorBox.CaretIndex = CommentEditorBox.Text?.Length ?? 0;
        }, DispatcherPriority.Input);
    }

    private async void OnOpenImageFileDialogRequested(object? sender, EventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose Image to Insert",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Images")
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp"]
                }
            ]
        });

        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ViewModel.CancelPendingImageSelection();
            return;
        }

        byte[] bytes = await File.ReadAllBytesAsync(path);
        ViewModel.SetPendingImage(bytes, Path.GetFileName(path));
    }

    private async void OnMergePdfDialogRequested(object? sender, EventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Merge PDF Document",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("PDF Documents")
                {
                    Patterns = ["*.pdf"]
                }
            ]
        });

        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            await ViewModel.MergePdfFileAsync(path);
        }
    }

    private void OnQuickFindKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            ViewModel.FindNextCommand.Execute(null);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            ViewModel.IsFindBarOpen = false;
        }
    }

    private void RepositionInlineEditor()
    {
        if (ViewModel is null || !ViewModel.IsEditingInline || ViewModel.EditingBlock is not { } block)
        {
            return;
        }

        var bounds = block.Bounds;
        if (double.TryParse(ViewModel.BlockXText, NumberStyles.Float, CultureInfo.InvariantCulture, out double x) &&
            double.TryParse(ViewModel.BlockYText, NumberStyles.Float, CultureInfo.InvariantCulture, out double y) &&
            double.TryParse(ViewModel.BlockWText, NumberStyles.Float, CultureInfo.InvariantCulture, out double w) &&
            double.TryParse(ViewModel.BlockHText, NumberStyles.Float, CultureInfo.InvariantCulture, out double h))
        {
            bounds = new PdfRect(x, y, x + Math.Max(24, w), y + Math.Max(12, h));
        }

        if (!PdfViewport.TryGetBlockScreenRect(block.PageIndex, bounds, out var screenRect, out _))
        {
            return;
        }

        const double extraWidth = 24.0;
        double left = ViewModel.TextAlignment switch
        {
            TextAlignMode.Center => screenRect.X - (extraWidth * 0.5),
            TextAlignMode.Right => screenRect.X - extraWidth,
            _ => screenRect.X - 1.0
        };
        double top = screenRect.Y;
        double width = Math.Max(36, screenRect.Width + extraWidth);
        double minHeight = Math.Max(14, screenRect.Height + 4);

        Canvas.SetLeft(InlineTextEditor, left);
        Canvas.SetTop(InlineTextEditor, top);
        InlineTextEditor.Width = width;
        InlineTextEditor.MinHeight = minHeight;
    }

    private async void OnInlineEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            await ViewModel.CommitInlineEditAsync();
        }
        else if (e.Key == Key.Enter &&
                 e.KeyModifiers == KeyModifiers.None &&
                 ViewModel.EditingBlock is { Lines.Count: <= 1 })
        {
            // Single-line fields (dates, headings, numbers) commit directly on Enter like Acrobat
            e.Handled = true;
            await ViewModel.CommitInlineEditAsync();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            ViewModel.CancelInlineEdit();
        }
    }

    private async void OnSaveFileDialogRequested(object? sender, EventArgs e)
    {
        if (ViewModel?.ActiveTab is null)
        {
            return;
        }

        var target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save PDF Document",
            SuggestedFileName = ViewModel.ActiveTab.FileName,
            DefaultExtension = "pdf",
            FileTypeChoices =
            [
                new FilePickerFileType("PDF Document")
                {
                    Patterns = ["*.pdf"]
                }
            ]
        });

        var localPath = target?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(localPath))
        {
            await ViewModel.SaveToPathAsync(localPath);
        }
    }

    private async void OnOpenFileDialogRequested(object? sender, EventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open PDF Document",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("PDF Documents")
                {
                    Patterns = ["*.pdf"]
                }
            ]
        });

        if (files.Count == 0)
        {
            return;
        }

        var localPaths = files
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Cast<string>()
            .ToArray();

        if (localPaths.Length > 0)
        {
            await ViewModel.OpenFilesAsync(localPaths);
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (ViewModel is null || !e.DataTransfer.Contains(DataFormat.File))
        {
            return;
        }

        var dropped = e.DataTransfer.TryGetFiles();
        if (dropped is null)
        {
            return;
        }

        var pdfPaths = dropped
            .Select(item => item.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path)
                        && path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            .Cast<string>()
            .ToArray();

        if (pdfPaths.Length > 0)
        {
            await ViewModel.OpenFilesAsync(pdfPaths);
            e.Handled = true;
        }
    }

    private void OnThumbnailContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container.DataContext is PageThumbnailViewModel thumb)
        {
            double dpi = RenderScaling;
            _ = thumb.EnsureLoadedAsync(Math.Max(1.25, dpi));
        }
    }

    private void OnThumbnailContainerClearing(object? sender, ContainerClearingEventArgs e)
    {
        if (e.Container.DataContext is PageThumbnailViewModel thumb)
        {
            thumb.CancelPendingRender();
        }
    }

    private void OnZoomPresetSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo && combo.SelectedItem is string preset && ViewModel is not null)
        {
            ViewModel.ApplyZoomPreset(preset);
            combo.SelectedItem = null;
        }
    }

    private void OnFontFamilySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && FontPickerButton is not null)
        {
            FontPickerButton.IsChecked = false;
        }
    }

    private void OnDocumentTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: DocumentTabViewModel tab } && ViewModel is not null)
        {
            ViewModel.ActiveTab = tab;
        }
    }

    private void OnCloseTabButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: DocumentTabViewModel tab } && ViewModel is not null)
        {
            ViewModel.CloseTabCommand.Execute(tab);
            e.Handled = true;
        }
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        // Only drag when pressing empty titlebar space (not buttons or tab items)
        if (e.Source is Control c && (c is Button || c.TemplatedParent is Button))
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximizeRestore();
        }
        else
        {
            BeginMoveDrag(e);
        }
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void OnMaximizeRestoreClick(object? sender, RoutedEventArgs e) =>
        ToggleMaximizeRestore();

    private void ToggleMaximizeRestore()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnCloseWindowClick(object? sender, RoutedEventArgs e) => Close();

    private void OnSettingsBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.IsSettingsOpen = false;
        }
    }

    private void OnSettingsCardPointerPressed(object? sender, PointerPressedEventArgs e) =>
        e.Handled = true;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || ViewModel is null || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        if (e.Key == Key.Escape && ViewModel.IsSettingsOpen)
        {
            ViewModel.IsSettingsOpen = false;
            e.Handled = true;
            return;
        }

        if (FocusManager?.GetFocusedElement() is TextBox)
        {
            return;
        }

        if (e.Key is Key.Delete or Key.Back && ViewModel.ActiveTab?.SelectedImage is not null)
        {
            _ = ViewModel.DeleteSelectedImageAsync();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.T)
        {
            ViewModel.SelectToolCommand.Execute(EditorTool.EditText);
            e.Handled = true;
        }
        else if (e.Key == Key.V || e.Key == Key.Escape)
        {
            ViewModel.SelectToolCommand.Execute(EditorTool.Select);
            e.Handled = true;
        }
    }
}
