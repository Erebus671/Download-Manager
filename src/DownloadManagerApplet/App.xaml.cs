using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using DownloadManagerApplet.Services;
using DownloadManagerApplet.ViewModels;

namespace DownloadManagerApplet;

public partial class App : Application
{
    private const int ExitForwarded = 0;
    private const int ExitStartupFailed = 1;
    private const int ExitForwardRejected = 2;
    private const int ExitPrimaryUnreachable = 3;

    private ILoggingService? _log;
    private HttpClient? _httpClient;
    private SingleInstanceGuard? _instanceGuard;
    private InstancePipeServer? _instanceServer;
    private MainWindow? _mainWindow;
    private MainViewModel? _mainViewModel;

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
        var launchUrls = ParseLaunchUrls(e.Args, _log);
        if (!TryAcquirePrimary(_log))
        {
            return;
        }

        if (!_instanceGuard!.IsPrimary)
        {
            ForwardToPrimary(launchUrls, _log);
            return;
        }

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

        _mainViewModel = mainViewModel;
        _mainWindow = new MainWindow(AppIcon.TryLoadSmall(_log)) { DataContext = mainViewModel };
        _mainWindow.Show();

        _instanceServer = new InstancePipeServer(InstanceNames.PipeName, HandleInstanceMessageAsync, _log);
        _instanceServer.Start();

        if (launchUrls.Count > 0)
        {
            mainViewModel.AddExternalDownloads(launchUrls);
        }
    }

    private bool TryAcquirePrimary(ILoggingService log)
    {
        try
        {
            _instanceGuard = new SingleInstanceGuard(InstanceNames.MutexName, log);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException or InvalidOperationException)
        {
            log.Error("Could not determine whether another instance is running", ex);
            ShowStartupError($"Could not check for another running instance:\n\n{ex.Message}");
            Shutdown(ExitStartupFailed);
            return false;
        }
    }

    private void ForwardToPrimary(IReadOnlyList<string> urls, ILoggingService log)
    {
        log.Info($"Another instance is running; forwarding {urls.Count} URL(s)");

        // Task.Run: no dispatcher context, so blocking here cannot deadlock the awaits inside SendAsync.
        var result = Task.Run(() => InstancePipeClient.SendAsync(InstanceNames.PipeName, InstanceMessage.FromUrls(urls), log))
            .GetAwaiter().GetResult();

        switch (result)
        {
            case InstanceSendResult.Accepted:
                Shutdown(ExitForwarded);
                break;
            case InstanceSendResult.Rejected:
                Shutdown(ExitForwardRejected);
                break;
            default:
                ShowStartupError("AtraTech Download Solutions is already running but is not responding.\n\nClose it from Task Manager and try again.");
                Shutdown(ExitPrimaryUnreachable);
                break;
        }
    }

    private async Task<bool> HandleInstanceMessageAsync(InstanceMessage message, CancellationToken cancellationToken)
    {
        var added = await Dispatcher.InvokeAsync(
            () =>
            {
                _mainWindow?.BringToFront();
                return _mainViewModel?.AddExternalDownloads(message.Urls) ?? 0;
            },
            DispatcherPriority.Normal,
            cancellationToken).Task;

        if (message.Urls.Count > 0)
        {
            _log?.Info($"Queued {added} of {message.Urls.Count} URL(s) from another process");
        }

        return added == message.Urls.Count;
    }

    private static List<string> ParseLaunchUrls(string[] args, ILoggingService log)
    {
        var urls = new List<string>();
        foreach (var arg in args)
        {
            if (Uri.TryCreate(arg, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                urls.Add(arg);
            }
            else
            {
                log.Warn($"Ignoring command-line argument that is not an http(s) URL: '{arg}'");
            }
        }

        if (urls.Count > InstanceMessage.MaxUrls)
        {
            log.Warn($"{urls.Count} URLs on the command line; only the first {InstanceMessage.MaxUrls} are used");
            urls.RemoveRange(InstanceMessage.MaxUrls, urls.Count - InstanceMessage.MaxUrls);
        }

        return urls;
    }

    private static void ShowStartupError(string message) =>
        MessageBox.Show(message, "AtraTech Download Solutions", MessageBoxButton.OK, MessageBoxImage.Error);

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
        if (_instanceServer is { } server)
        {
            // Off the dispatcher: DisposeAsync awaits the listener, which may be waiting on this thread.
            Task.Run(() => server.DisposeAsync().AsTask()).GetAwaiter().GetResult();
        }

        _instanceGuard?.Dispose();
        _httpClient?.Dispose();
        (_log as IDisposable)?.Dispose();
        base.OnExit(e);
    }
}
