using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PdfStudio.App.ViewModels;
using PdfStudio.App.Views;
using PdfStudio.Core.Pdfium;

namespace PdfStudio.App;

public partial class App : Application
{
    private MainWindowViewModel? _mainViewModel;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        PdfiumEngine.Instance.EnsureInitialized();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _mainViewModel = new MainWindowViewModel();
            var mainWindow = new MainWindow
            {
                DataContext = _mainViewModel
            };

            desktop.MainWindow = mainWindow;

            if (desktop.Args is { Length: > 0 })
            {
                var pdfArgs = desktop.Args.Where(a =>
                    File.Exists(a) && a.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (pdfArgs.Length > 0)
                {
                    _ = _mainViewModel.OpenFilesAsync(pdfArgs);
                }
            }

            desktop.Exit += (_, _) =>
            {
                _mainViewModel?.Dispose();
                PdfiumEngine.Instance.Shutdown();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
