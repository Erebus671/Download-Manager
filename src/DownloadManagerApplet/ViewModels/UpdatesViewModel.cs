using DownloadManagerApplet.Models;
using DownloadManagerApplet.Mvvm;
using DownloadManagerApplet.Services;
using DownloadManagerApplet.Services.Updates;

namespace DownloadManagerApplet.ViewModels;

public enum UpdateStatus
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    Ready,
    Installing,
    Failed
}

public enum UpdatePromptResult
{
    Install,
    Later,
    Skip
}

public sealed record UpdateFrequencyOption(UpdateCheckFrequency Value, string Label);

/// <summary>Update check, download, and prompt state. Public members are called on the UI thread.</summary>
public sealed class UpdatesViewModel : ObservableObject
{
    private const string Dot = "●";
    private const string Warning = "⚠";

    private static readonly TimeSpan LaunchCheckDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(15);

    private readonly AppState _state;
    private readonly Action _persist;
    private readonly IReleaseSource _source;
    private readonly IUpdateDownloader _downloader;
    private readonly Version _currentVersion;
    private readonly ILoggingService _log;
    private readonly TimeProvider _time;

    private UpdateStatus _status = UpdateStatus.Idle;
    private string _statusSummary = "Not checked yet";
    private string? _statusBarText;
    private bool _statusBarIsError;
    private ReleaseInfo? _available;
    private VerifiedUpdate? _ready;
    private bool _promptDeferred;
    private bool _busy;
    private ITimer? _timer;
    private SynchronizationContext? _uiContext;

    public UpdatesViewModel(
        AppState state,
        Action persist,
        IReleaseSource source,
        IUpdateDownloader downloader,
        Version currentVersion,
        ILoggingService log,
        TimeProvider time)
    {
        _state = state;
        _persist = persist;
        _source = source;
        _downloader = downloader;
        _currentVersion = currentVersion;
        _log = log;
        _time = time;

        CheckNowCommand = new RelayCommand(() => Fire(CheckAsync(manual: true)), () => !_busy);
        StatusBarCommand = new RelayCommand(OnStatusBarClicked, () => StatusBarIsClickable);
    }

    public string CurrentVersion => UpdateSignatureFormat.NormalizeVersion(_currentVersion);

    public IReadOnlyList<UpdateFrequencyOption> Frequencies { get; } =
    [
        new(UpdateCheckFrequency.LaunchAndDaily, "At launch and every 24 hours"),
        new(UpdateCheckFrequency.LaunchOnly, "At launch only"),
        new(UpdateCheckFrequency.Every6Hours, "Every 6 hours"),
        new(UpdateCheckFrequency.Weekly, "Weekly")
    ];

    public RelayCommand CheckNowCommand { get; }
    public RelayCommand StatusBarCommand { get; }

    /// <summary>Raised when a verified update should be offered to the user.</summary>
    public event Action<VerifiedUpdate>? PromptRequested;

    /// <summary>Set by the app: pauses downloads, starts the helper, and shuts down. Returns false if the update could not start.</summary>
    public Func<VerifiedUpdate, Task<bool>>? InstallHandler { get; set; }

    public UpdateStatus Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                RefreshCommands();
            }
        }
    }

    public string StatusSummary
    {
        get => _statusSummary;
        private set => SetProperty(ref _statusSummary, value);
    }

    public string? StatusBarText
    {
        get => _statusBarText;
        private set => SetProperty(ref _statusBarText, value);
    }

    public bool StatusBarIsError
    {
        get => _statusBarIsError;
        private set => SetProperty(ref _statusBarIsError, value);
    }

    public bool StatusBarIsClickable => Status is UpdateStatus.Ready || (Status is UpdateStatus.Available && !IsSkipped(_available));

    public string LastCheckedDisplay => _state.Updates.LastCheck is { } t ? FormatWhen(t.ToLocalTime()) : "Never";

    public void Start(AfterUpdateInfo? afterUpdate)
    {
        _uiContext = SynchronizationContext.Current;
        if (afterUpdate is not null)
        {
            ReportAfterUpdate(afterUpdate);
        }

        var firstDue = _state.Settings.CheckForUpdatesAutomatically ? LaunchCheckDelay : TickInterval;
        _timer = _time.CreateTimer(_ => Post(OnTimerTick), null, firstDue, TickInterval);
    }

    /// <summary>Leaves updates off for this session (e.g. a build without trusted keys).</summary>
    public void Disable(string reason)
    {
        _busy = true;
        StatusSummary = reason;
        RefreshCommands();
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public async Task CheckAsync(bool manual)
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true);
        Status = UpdateStatus.Checking;
        StatusSummary = "Checking...";
        try
        {
            ReleaseInfo? release;
            try
            {
                release = await _source.GetLatestAsync(_state.Settings.IncludePrereleaseUpdates, CancellationToken.None);
            }
            catch (UpdateCheckException ex)
            {
                _log.Warn($"Update check failed: {ex.Message}");
                Status = UpdateStatus.Failed;
                StatusSummary = $"Check failed: {ex.Message}";
                return;
            }

            _state.Updates.LastCheck = _time.GetLocalNow();
            _persist();
            OnPropertyChanged(nameof(LastCheckedDisplay));

            if (release is null || release.Version <= _currentVersion)
            {
                _log.Debug($"Update check: up to date ({CurrentVersion}; latest {release?.Version.ToString(3) ?? "none"})");
                _available = null;
                _ready = null;
                Status = UpdateStatus.UpToDate;
                StatusSummary = "Up to date";
                ClearStatusBar();
                _downloader.CleanUp(keep: null);
                return;
            }

            _available = release;
            var v = release.Version.ToString(3);
            _log.Info($"Update check: {v} available{(release.IsPrerelease ? " (pre-release)" : string.Empty)}");

            if (!manual && IsSkipped(release))
            {
                Status = UpdateStatus.Available;
                StatusSummary = $"{v} available (skipped)";
                ClearStatusBar();
                return;
            }

            if (manual)
            {
                _promptDeferred = false;
            }

            if (manual || _state.Settings.DownloadUpdatesAutomatically)
            {
                await DownloadCoreAsync(release);
            }
            else
            {
                Status = UpdateStatus.Available;
                StatusSummary = $"{v} available";
                SetStatusBar($"Update {v} available: click to download", isError: false);
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    public void OnPromptResult(VerifiedUpdate update, UpdatePromptResult result)
    {
        var v = update.Manifest.Version.ToString(3);
        switch (result)
        {
            case UpdatePromptResult.Install:
                Fire(InstallAsync(update));
                break;
            case UpdatePromptResult.Later:
                _promptDeferred = true;
                _log.Info($"Update {v}: postponed");
                break;
            case UpdatePromptResult.Skip:
                _state.Updates.SkippedVersion = v;
                _persist();
                _log.Info($"Update {v}: skipped until a newer release");
                _ready = null;
                Status = UpdateStatus.Available;
                StatusSummary = $"{v} available (skipped)";
                ClearStatusBar();
                _downloader.CleanUp(keep: null);
                break;
        }
    }

    private async Task DownloadCoreAsync(ReleaseInfo release)
    {
        var v = release.Version.ToString(3);
        Status = UpdateStatus.Downloading;
        StatusSummary = $"Downloading {v}...";
        SetStatusBar($"Downloading update {v}... 0%", isError: false);

        var progress = new Progress<double>(p =>
        {
            if (Status == UpdateStatus.Downloading)
            {
                StatusBarText = $"Downloading update {v}... {p:0}%";
            }
        });

        try
        {
            _downloader.CleanUp(keep: release.Version);
            var update = await _downloader.DownloadAsync(release, progress, CancellationToken.None);
            _ready = update;
            Status = UpdateStatus.Ready;
            StatusSummary = $"{v} ready to install";
            SetStatusBar($"{Dot} Update {v} ready: click to install", isError: false);

            if (!_promptDeferred)
            {
                PromptRequested?.Invoke(update);
            }
        }
        catch (UpdateSignatureException ex)
        {
            _log.Error($"Update {v} failed signature check and was deleted: {ex.Message}");
            Status = UpdateStatus.Failed;
            StatusSummary = $"{v} failed signature check";
            SetStatusBar($"{Warning} Update {v} failed signature check and was deleted (see log)", isError: true);
        }
        catch (UpdateCheckException ex)
        {
            _log.Warn($"Update {v} download failed: {ex.Message}");
            Status = UpdateStatus.Failed;
            StatusSummary = $"{v} download failed";
            SetStatusBar($"{Warning} Update {v} download failed (see log)", isError: true);
        }
    }

    private async Task InstallAsync(VerifiedUpdate update)
    {
        var v = update.Manifest.Version.ToString(3);
        if (InstallHandler is null)
        {
            _log.Error($"Update {v}: no install handler registered");
            return;
        }

        Status = UpdateStatus.Installing;
        StatusSummary = $"Installing {v}...";
        SetStatusBar($"Preparing update {v}...", isError: false);

        try
        {
            if (await InstallHandler(update))
            {
                return;
            }
        }
        catch (UpdateCheckException ex)
        {
            _log.Error($"Update {v} could not start: {ex.Message}");
        }

        _promptDeferred = true;
        Status = UpdateStatus.Ready;
        StatusSummary = $"{v} ready to install";
        SetStatusBar($"{Warning} Update {v} could not start (see log); click to retry", isError: true);
    }

    private void ReportAfterUpdate(AfterUpdateInfo info)
    {
        var v = info.TargetVersion.ToString(3);
        switch (info.Outcome)
        {
            case UpdateOutcome.Installed when _currentVersion >= info.TargetVersion:
                _log.Info($"Updated to {CurrentVersion} (installer exit {info.ExitCode})");
                SetStatusBar($"Updated to {CurrentVersion}", isError: false);
                break;
            case UpdateOutcome.Installed:
                _log.Error($"Installer for {v} reported success (exit {info.ExitCode}) but this is still {CurrentVersion}");
                SetStatusBar($"{Warning} Update {v} did not install (see log)", isError: true);
                _promptDeferred = true;
                break;
            case UpdateOutcome.Canceled:
                _log.Info($"Update {v} was canceled (exit {info.ExitCode})");
                SetStatusBar($"Update {v} was canceled", isError: false);
                _promptDeferred = true;
                break;
            default:
                _log.Error($"Update {v} did not install (exit {info.ExitCode})");
                SetStatusBar($"{Warning} Update {v} did not install (code {info.ExitCode}, see log)", isError: true);
                _promptDeferred = true;
                break;
        }
    }

    private void OnTimerTick()
    {
        var s = _state.Settings;
        if (!s.CheckForUpdatesAutomatically || _busy)
        {
            return;
        }

        var launchCheckDone = _status != UpdateStatus.Idle;
        if (!launchCheckDone)
        {
            Fire(CheckAsync(manual: false));
            return;
        }

        TimeSpan? interval = s.UpdateCheckFrequency switch
        {
            UpdateCheckFrequency.LaunchAndDaily => TimeSpan.FromHours(24),
            UpdateCheckFrequency.Every6Hours => TimeSpan.FromHours(6),
            UpdateCheckFrequency.Weekly => TimeSpan.FromDays(7),
            _ => null
        };

        if (interval is { } i && (_state.Updates.LastCheck is not { } last || _time.GetLocalNow() - last >= i))
        {
            Fire(CheckAsync(manual: false));
        }
    }

    private void OnStatusBarClicked()
    {
        if (Status == UpdateStatus.Ready && _ready is { } ready)
        {
            PromptRequested?.Invoke(ready);
        }
        else if (Status == UpdateStatus.Available && _available is { } release && !_busy)
        {
            Fire(DownloadWithBusyAsync(release));
        }
    }

    private async Task DownloadWithBusyAsync(ReleaseInfo release)
    {
        SetBusy(true);
        try
        {
            _promptDeferred = false;
            await DownloadCoreAsync(release);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private bool IsSkipped(ReleaseInfo? release) =>
        release is not null && string.Equals(_state.Updates.SkippedVersion, release.Version.ToString(3), StringComparison.Ordinal);

    private void SetStatusBar(string text, bool isError)
    {
        StatusBarText = text;
        StatusBarIsError = isError;
        RefreshCommands();
    }

    private void ClearStatusBar() => SetStatusBar(string.Empty, isError: false);

    private void SetBusy(bool busy)
    {
        _busy = busy;
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        OnPropertyChanged(nameof(StatusBarIsClickable));
        CheckNowCommand.NotifyCanExecuteChanged();
        StatusBarCommand.NotifyCanExecuteChanged();
    }

    private void Post(Action action)
    {
        if (_uiContext is { } ctx)
        {
            ctx.Post(_ => action(), null);
        }
        else
        {
            action();
        }
    }

    private void Fire(Task task) =>
        task.ContinueWith(
            t => _log.Error("Unexpected update failure", t.Exception!.GetBaseException()),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

    private string FormatWhen(DateTimeOffset when)
    {
        var today = _time.GetLocalNow().Date;
        var day = when.Date == today ? "Today" : when.Date == today.AddDays(-1) ? "Yesterday" : when.ToString("MMM d, yyyy");
        return $"{day}, {when:h:mm tt}";
    }
}
