using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using DownloadManagerApplet.Services;
using DownloadManagerApplet.ViewModels;

namespace DownloadManagerApplet;

public partial class App : Application
{
    private ILoggingService? _log;
    private HttpClient? _httpClient;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var appDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DownloadManagerApplet");

        var store = new JsonAppStore(Path.Combine(appDataFolder, "state.json"), NullLoggingService.Instance);
        var state = store.Load();

        _log = new FileLoggingService(Path.Combine(appDataFolder, "logs"), state.Settings.MinimumLogLevel);

        // JsonAppStore was constructed before the real logger existed; rebuild it now so load/save
        // failures land in the real log rather than being silently swallowed.
        var appStore = new JsonAppStore(Path.Combine(appDataFolder, "state.json"), _log);

        _httpClient = new HttpClient();
        var engine = new HttpDownloadEngine(_httpClient, _log);
        var orchestrator = new DownloadOrchestrator(
            engine,
            _log,
            () => state.Settings.MaxConcurrentDownloads,
            () => state.Settings.MaxRetryAttempts);

        var mainViewModel = new MainViewModel(state, appStore, orchestrator, _log);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        var window = new MainWindow { DataContext = mainViewModel };
        window.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.Error("Unhandled UI exception", e.Exception);
        MessageBox.Show(
            $"An unexpected error occurred and has been logged:\n\n{e.Exception.Message}",
            "AtraTech Download Solutions",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            _log?.Error("Unhandled background exception", ex);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _httpClient?.Dispose();
        (_log as IDisposable)?.Dispose();
        base.OnExit(e);
    }
}
