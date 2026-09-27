using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows;
using System.Windows.Threading;
using DownloadManagerApplet.Services;
using DownloadManagerApplet.Services.Browser;
using DownloadManagerApplet.Services.Updates;
using Microsoft.Win32;
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
    private HttpClient? _updateHttpClient;
    private DispatcherTimer? _browserStatusTimer;
    private UpdatesViewModel? _updates;
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
        var logFolder = Path.Combine(appDataFolder, "logs");
        var updatesFolder = Path.Combine(appDataFolder, "updates");
        var args = e.Args.ToList();

        if (args.Count > 0 && args[0] == UpdateApplier.Flag)
        {
            RunUpdateHelper(args, logFolder);
            return;
        }

        if (args.Count > 0 && args[0] == NativeHostRegistration.UnregisterFlag)
        {
            RunUnregisterBrowser(appDataFolder, logFolder);
            return;
        }

        var afterUpdate = AfterUpdateInfo.Extract(args);
        var startInBackground = args.Remove(InstanceNames.BackgroundFlag);

        var store = new JsonAppStore(Path.Combine(appDataFolder, "state.json"), NullLoggingService.Instance);
        var state = store.Load();

        _log = new FileLoggingService(logFolder, state.Settings.MinimumLogLevel);

        var launchUrls = ParseLaunchUrls(args, _log);
        if (!TryAcquirePrimary(_log))
        {
            return;
        }

        if (!_instanceGuard!.IsPrimary)
        {
            ForwardToPrimary(launchUrls, _log);
            return;
        }

        // Rebuilt with the real logger so load/save failures are not swallowed.
        var appStore = new JsonAppStore(Path.Combine(appDataFolder, "state.json"), _log);

        _httpClient = new HttpClient();
        var planner = new DestinationPlanner(state, _log);
        var engine = new HttpDownloadEngine(_httpClient, _log, planner);
        var orchestrator = new DownloadOrchestrator(
            engine,
            _log,
            () => state.Settings.MaxConcurrentDownloads,
            () => state.Settings.MaxRetryAttempts);

        var updates = CreateUpdates(state, appStore, updatesFolder, out var updatesEnabled);
        var mainViewModel = new MainViewModel(state, appStore, orchestrator, _log, updates, planner);
        updates.InstallHandler = update => InstallUpdateAsync(update, mainViewModel, updatesFolder, logFolder);
        _updates = updates;
        var browserService = CreateBrowserIntegration(state, appStore, mainViewModel, appDataFolder);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        _mainViewModel = mainViewModel;
        _mainWindow = new MainWindow(AppIcon.TryLoadSmall(_log)) { DataContext = mainViewModel };
        if (startInBackground)
        {
            _mainWindow.ShowActivated = false;
            _mainWindow.WindowState = WindowState.Minimized;
        }

        _mainWindow.Show();

        _instanceServer = new InstancePipeServer(InstanceNames.PipeName, HandleInstanceMessageAsync, _log, browserHandler: browserService.HandleAsync);
        _instanceServer.Start();

        if (launchUrls.Count > 0)
        {
            mainViewModel.AddExternalDownloads(launchUrls);
        }

        if (updatesEnabled)
        {
            updates.Start(afterUpdate);
        }
    }

    private static Version CurrentVersion => typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0);

    private UpdatesViewModel CreateUpdates(AppState state, IAppStore appStore, string updatesFolder, out bool enabled)
    {
        var log = _log!;
        var version = UpdateSignatureFormat.NormalizeVersion(CurrentVersion);
        _updateHttpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        _updateHttpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AtraTechDownloadSolutions", version));

        var verifier = new UpdateSignatureVerifier(TrustedUpdateKeys.All);
        var updates = new UpdatesViewModel(
            state,
            () => appStore.Save(state),
            new GitHubReleaseSource(_updateHttpClient, "Erebus671", "Download-Manager", log),
            new UpdateDownloader(_updateHttpClient, verifier, updatesFolder, log),
            Version.Parse(version),
            log,
            TimeProvider.System);

        enabled = verifier.HasTrustedKeys;
        if (!enabled)
        {
            log.Warn("This build has no trusted update keys; automatic updates are disabled");
            updates.Disable("Updates disabled in this build");
        }

        return updates;
    }

    private BrowserIntegrationService CreateBrowserIntegration(AppState state, IAppStore appStore, MainViewModel mainViewModel, string appDataFolder)
    {
        var log = _log!;
        var registration = CreateHostRegistration(appDataFolder, log);

        // Re-register each start so an update or moved install self-heals.
        if (state.Settings.BrowserIntegration.Enabled)
        {
            registration.Register();
        }

        var browserVm = new BrowserIntegrationViewModel(state, registration, log);
        mainViewModel.BrowserIntegration = browserVm;

        var service = new BrowserIntegrationService(
            state,
            (action, ct) => Dispatcher.InvokeAsync(action, DispatcherPriority.Normal, ct).Task,
            () => appStore.Save(state),
            mainViewModel.AddBrowserDownload,
            openSettings =>
            {
                _mainWindow?.BringToFront();
                if (openSettings)
                {
                    _mainWindow?.ShowBrowserSettings();
                }
            },
            new HttpHandoffProbe(),
            log,
            UpdateSignatureFormat.NormalizeVersion(CurrentVersion));
        service.ConnectionsChanged += browserVm.RefreshConnections;
        service.SettingsChanged += browserVm.RefreshFromSettings;

        // Ages rows to "Not connected" when a browser stops checking in, and picks up connector changes made outside the app.
        _browserStatusTimer = new DispatcherTimer(BrowserIntegrationViewModel.StatusRefreshInterval, DispatcherPriority.Background, (_, _) =>
        {
            browserVm.RefreshConnections();
            browserVm.RefreshHostStatus();
        }, Dispatcher);
        _browserStatusTimer.Start();
        return service;
    }

    private async Task<bool> InstallUpdateAsync(VerifiedUpdate update, MainViewModel mainViewModel, string updatesFolder, string logFolder)
    {
        if (!await mainViewModel.PrepareForUpdateAsync(update.Manifest.Version))
        {
            throw new UpdateCheckException("Downloads did not pause in time");
        }

        try
        {
            UpdateLauncher.StartHelper(update, updatesFolder, logFolder, _log!);
        }
        catch (UpdateCheckException)
        {
            mainViewModel.CancelUpdatePreparation();
            throw;
        }

        _updates?.Stop();
        Shutdown(ExitForwarded);
        return true;
    }

    private void RunUpdateHelper(List<string> args, string logFolder)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var log = new FileLoggingService(logFolder, Models.LogLevelSetting.Info);
        _log = log;

        var request = UpdateApplier.ParseRequest(args);
        if (request is null)
        {
            log.Error($"Update helper: invalid arguments ({args.Count})");
            Shutdown(ExitStartupFailed);
            return;
        }

        int code;
        try
        {
            var applier = new UpdateApplier(log, new UpdateSignatureVerifier(TrustedUpdateKeys.All), CurrentVersion);
            code = applier.Run(request);
        }
        catch (Exception ex)
        {
            log.Error("Update helper: unexpected failure", ex);
            code = ExitStartupFailed;
        }

        Shutdown(code);
    }

    /// <summary>Uninstaller entry point: removes this user's browser connector registration, then exits without UI.</summary>
    private void RunUnregisterBrowser(string appDataFolder, string logFolder)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var log = new FileLoggingService(logFolder, Models.LogLevelSetting.Info);
        _log = log;

        int code;
        try
        {
            code = CreateHostRegistration(appDataFolder, log).Unregister() ? 0 : ExitStartupFailed;
        }
        catch (Exception ex)
        {
            log.Error("Unregister browser connector: unexpected failure", ex);
            code = ExitStartupFailed;
        }

        Shutdown(code);
    }

    private static NativeHostRegistration CreateHostRegistration(string appDataFolder, ILoggingService log) => new(
        Registry.CurrentUser,
        Path.Combine(appDataFolder, "NativeMessaging"),
        Path.Combine(AppContext.BaseDirectory, NativeHostRegistration.HostExeName),
        log);

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

    private static List<string> ParseLaunchUrls(IEnumerable<string> args, ILoggingService log)
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

        _browserStatusTimer?.Stop();
        _instanceGuard?.Dispose();
        _httpClient?.Dispose();
        _updateHttpClient?.Dispose();
        (_log as IDisposable)?.Dispose();
        base.OnExit(e);
    }
}
