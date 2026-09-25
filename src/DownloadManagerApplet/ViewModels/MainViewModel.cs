using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using DownloadManagerApplet.Models;
using DownloadManagerApplet.Mvvm;
using DownloadManagerApplet.Services;

namespace DownloadManagerApplet.ViewModels;

public enum SaveTargetKind
{
    Automatic,
    Category,
    Folder,
    Browse
}

/// <summary>One entry of the add bar's "Save to" list.</summary>
public sealed record SaveTargetOption(SaveTargetKind Kind, string Label, FileCategory? Category = null, string? Folder = null)
{
    public override string ToString() => Label;
}

public sealed class MainViewModel : ObservableObject
{
    private readonly IAppStore _store;
    private readonly DownloadOrchestrator _orchestrator;
    private readonly ILoggingService _log;
    private readonly AppState _state;
    private readonly List<Guid> _resumeAfterUpdate = new();
    private readonly DestinationPlanner _planner;
    private DispatcherTimer? _saveMessageTimer;

    private static readonly TimeSpan PendingResumeMaxAge = TimeSpan.FromHours(2);
    private static readonly TimeSpan SuspendTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SaveMessageDuration = TimeSpan.FromSeconds(4);

    private string _newDownloadUrl = string.Empty;
    private SaveTargetOption _selectedSaveTarget;
    private SaveTargetOption _lastRealSaveTarget;
    private string? _settingsSaveMessage;
    private bool _settingsSaveFailed;

    public ObservableCollection<DownloadItemViewModel> Queue { get; } = new();
    public ObservableCollection<DownloadItemViewModel> History { get; } = new();
    public ReadOnlyObservableCollection<LogEntry> LogEntries => _log.RecentEntries;

    public AppSettings Settings => _state.Settings;
    public UpdatesViewModel? Updates { get; }
    public IReadOnlyList<LogLevelSetting> LogLevels { get; } = Enum.GetValues<LogLevelSetting>();
    public DestinationSettingsViewModel Destinations { get; }
    public ObservableCollection<SaveTargetOption> SaveTargets { get; } = new();

    /// <summary>Folder picker for "Choose folder..."; replaceable for tests. Takes the starting folder, returns the choice or null.</summary>
    public Func<string, string?> PickFolder
    {
        get => Destinations.PickFolder;
        set => Destinations.PickFolder = value;
    }

    public bool HasRestoredPendingDownloads { get; }

    /// <summary>Downloads paused for an update; resumed by <see cref="ResumeAfterUpdate"/> with no prompt.</summary>
    public bool HasDownloadsToResumeAfterUpdate => _resumeAfterUpdate.Count > 0;

    public int ActiveDownloadCount => Queue.Count(v => v.Status is DownloadStatus.Downloading or DownloadStatus.Queued);

    public string NewDownloadUrl
    {
        get => _newDownloadUrl;
        set => SetProperty(ref _newDownloadUrl, value);
    }

    public SaveTargetOption SelectedSaveTarget
    {
        get => _selectedSaveTarget;
        set
        {
            if (value is null || !SetProperty(ref _selectedSaveTarget, value))
            {
                return;
            }

            if (value.Kind != SaveTargetKind.Browse)
            {
                _lastRealSaveTarget = value;
                return;
            }

            // From the ComboBox: let it finish its selection change before the dialog opens.
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is not null && dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(ChooseSaveFolder);
            }
            else
            {
                ChooseSaveFolder();
            }
        }
    }

    /// <summary>"Settings saved", or why saving failed; clears itself after a few seconds.</summary>
    public string? SettingsSaveMessage
    {
        get => _settingsSaveMessage;
        private set => SetProperty(ref _settingsSaveMessage, value);
    }

    public bool SettingsSaveFailed
    {
        get => _settingsSaveFailed;
        private set => SetProperty(ref _settingsSaveFailed, value);
    }

    public RelayCommand AddDownloadCommand { get; }
    public RelayCommand PauseAllCommand { get; }
    public RelayCommand ResumeAllCommand { get; }
    public RelayCommand ClearCompletedCommand { get; }
    public RelayCommand SaveSettingsCommand { get; }

    public MainViewModel(
        AppState state,
        IAppStore store,
        DownloadOrchestrator orchestrator,
        ILoggingService log,
        UpdatesViewModel? updates = null,
        DestinationPlanner? planner = null)
    {
        Updates = updates;
        _state = state;
        _store = store;
        _orchestrator = orchestrator;
        _log = log;
        _planner = planner ?? new DestinationPlanner(state, log);
        _planner.DestinationChanged += OnDestinationChanged;
        Destinations = new DestinationSettingsViewModel(state.Settings, () => Persist());

        BuildSaveTargets();
        _selectedSaveTarget = _lastRealSaveTarget = SaveTargets[0];

        AddDownloadCommand = new RelayCommand(AddDownload);
        PauseAllCommand = new RelayCommand(PauseAll);
        ResumeAllCommand = new RelayCommand(ResumeAll);
        ClearCompletedCommand = new RelayCommand(ClearCompleted);
        SaveSettingsCommand = new RelayCommand(SaveSettings);

        foreach (var item in _state.Downloads)
        {
            // No live task survives a restart, so running and waiting items come back as Paused.
            if (item.Status is DownloadStatus.Downloading or DownloadStatus.Queued)
            {
                item.Status = DownloadStatus.Paused;
            }

            AddViewModelFor(item);
        }

        TakePendingUpdateResume();
        HasRestoredPendingDownloads = !HasDownloadsToResumeAfterUpdate && _state.Downloads.Any(d => d.Status == DownloadStatus.Paused);

        _orchestrator.StateChanged += OnOrchestratorStateChanged;
    }

    private void AddDownload()
    {
        var target = SelectedSaveTarget.Kind == SaveTargetKind.Browse ? _lastRealSaveTarget : SelectedSaveTarget;
        var folder = target.Kind switch
        {
            SaveTargetKind.Category => DestinationResolver.CategoryFolder(Settings, target.Category ?? FileCategory.Other),
            SaveTargetKind.Folder => target.Folder,
            _ => null
        };

        if (TryAddDownload(NewDownloadUrl, folder))
        {
            NewDownloadUrl = string.Empty;
        }
    }

    /// <summary>Queues URLs handed over by another process, with the folder chosen automatically. Returns the number queued.</summary>
    public int AddExternalDownloads(IEnumerable<string> urls)
    {
        var added = 0;
        foreach (var url in urls)
        {
            if (TryAddDownload(url, null))
            {
                added++;
            }
        }

        return added;
    }

    /// <summary>A null <paramref name="folder"/> means Automatic (rules, then file type).</summary>
    private bool TryAddDownload(string rawUrl, string? folder)
    {
        var url = rawUrl.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            _log.Warn($"Not a valid http(s) URL: '{url}'");
            return false;
        }

        if (folder is not null && !DestinationResolver.IsUsableFolder(folder))
        {
            _log.Warn($"Folder '{folder}' is not usable; choosing one automatically for {url}");
            folder = null;
        }

        var item = _planner.CreateItem(url, uri, folder);
        _state.Downloads.Add(item);
        var vm = AddViewModelFor(item);
        _orchestrator.Enqueue(item, vm);
        Persist();
        return true;
    }

    private void BuildSaveTargets()
    {
        SaveTargets.Add(new SaveTargetOption(SaveTargetKind.Automatic, "Automatic (by file type)"));
        foreach (var category in DestinationResolver.SortedCategories.Append(FileCategory.Other))
        {
            SaveTargets.Add(new SaveTargetOption(SaveTargetKind.Category, DestinationResolver.DisplayName(category), category));
        }

        SaveTargets.Add(new SaveTargetOption(SaveTargetKind.Browse, "Choose folder..."));
    }

    private void ChooseSaveFolder()
    {
        var start = _lastRealSaveTarget.Kind switch
        {
            SaveTargetKind.Folder => _lastRealSaveTarget.Folder!,
            SaveTargetKind.Category => DestinationResolver.CategoryFolder(Settings, _lastRealSaveTarget.Category ?? FileCategory.Other),
            _ => Settings.DefaultDownloadFolder
        };

        string? chosen;
        try
        {
            chosen = PickFolder(start);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _log.Error("Could not open the folder picker", ex);
            chosen = null;
        }

        if (string.IsNullOrWhiteSpace(chosen))
        {
            SelectedSaveTarget = _lastRealSaveTarget;
            return;
        }

        var existing = SaveTargets.FirstOrDefault(t => t.Kind == SaveTargetKind.Folder && string.Equals(t.Folder, chosen, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            existing = new SaveTargetOption(SaveTargetKind.Folder, chosen, Folder: chosen);
            SaveTargets.Insert(SaveTargets.Count - 1, existing);
        }

        SelectedSaveTarget = existing;
    }

    /// <summary>Shows the rename dialog; set by the window. Returns true when the user renamed.</summary>
    public Func<RenameViewModel, bool>? ShowRenameDialog { get; set; }

    private void Rename(DownloadItemViewModel vm)
    {
        if (ShowRenameDialog is null)
        {
            return;
        }

        // Success raises DestinationChanged, which refreshes the card and persists.
        ShowRenameDialog(new RenameViewModel(vm.FileName, name => _planner.Rename(vm.Model, name)));
    }

    private void OnDestinationChanged(DownloadItem item)
    {
        foreach (var vm in Queue.Concat(History).Where(v => ReferenceEquals(v.Model, item)).ToList())
        {
            vm.RefreshFromModel();
        }

        Persist();
    }

    private void PauseAll()
    {
        foreach (var vm in Queue.Where(v => v.Status == DownloadStatus.Downloading).ToList())
        {
            _orchestrator.Pause(vm.Model);
        }
    }

    private void ResumeAll()
    {
        foreach (var vm in Queue.Where(v => v.Status is DownloadStatus.Paused or DownloadStatus.Error).ToList())
        {
            _orchestrator.Resume(vm.Model, vm);
        }
    }

    private void TakePendingUpdateResume()
    {
        if (_state.Updates.PendingResume is not { } pending)
        {
            return;
        }

        _state.Updates.PendingResume = null;
        var age = DateTimeOffset.Now - pending.CreatedAt;
        if (age > PendingResumeMaxAge || age < TimeSpan.Zero)
        {
            _log.Warn($"Ignoring downloads paused for update {pending.TargetVersion}: recorded {age.TotalMinutes:0} min ago; asking instead");
            return;
        }

        _resumeAfterUpdate.AddRange(pending.DownloadIds.Where(id => _state.Downloads.Any(d => d.Id == id && d.Status == DownloadStatus.Paused)));
        _log.Info($"{_resumeAfterUpdate.Count} download(s) paused for update {pending.TargetVersion} will resume");
    }

    /// <summary>Resumes the downloads that were paused for an update install.</summary>
    public void ResumeAfterUpdate()
    {
        foreach (var vm in Queue.Where(v => _resumeAfterUpdate.Contains(v.Model.Id)).ToList())
        {
            _orchestrator.Resume(vm.Model, vm);
        }

        _resumeAfterUpdate.Clear();
        Persist();
    }

    /// <summary>Pauses running and queued downloads and records them for resume after the update. Returns false if downloads would not stop.</summary>
    public async Task<bool> PrepareForUpdateAsync(Version targetVersion)
    {
        var ids = _state.Downloads.Where(d => d.Status is DownloadStatus.Downloading or DownloadStatus.Queued).Select(d => d.Id).ToList();
        _log.Info($"Preparing for update {targetVersion}: pausing {ids.Count} download(s)");

        if (!await _orchestrator.SuspendAsync(SuspendTimeout))
        {
            _log.Error("Downloads did not stop in time; update postponed");
            CancelUpdatePreparation(ids);
            return false;
        }

        foreach (var item in _state.Downloads.Where(d => ids.Contains(d.Id)))
        {
            item.Status = DownloadStatus.Paused;
        }

        _state.Updates.PendingResume = new PendingUpdateResume
        {
            DownloadIds = ids,
            CreatedAt = DateTimeOffset.Now,
            TargetVersion = targetVersion.ToString(3)
        };

        foreach (var vm in Queue.ToList())
        {
            vm.RefreshFromModel();
        }

        Persist();
        return true;
    }

    /// <summary>Undoes <see cref="PrepareForUpdateAsync"/> when the update cannot start.</summary>
    public void CancelUpdatePreparation(IReadOnlyCollection<Guid>? ids = null)
    {
        _orchestrator.Unsuspend();
        var toResume = ids ?? _state.Updates.PendingResume?.DownloadIds ?? new List<Guid>();
        _state.Updates.PendingResume = null;

        foreach (var vm in Queue.Where(v => toResume.Contains(v.Model.Id) && v.Model.Status is DownloadStatus.Paused).ToList())
        {
            _orchestrator.Resume(vm.Model, vm);
        }

        Persist();
    }

    private void ClearCompleted()
    {
        var completed = History.Where(v => v.Status == DownloadStatus.Completed).ToList();
        foreach (var vm in completed)
        {
            History.Remove(vm);
            _state.Downloads.Remove(vm.Model);
        }

        Persist();
    }

    private void SaveSettings()
    {
        var problem = ValidateSettings();
        if (problem is not null)
        {
            _log.Warn($"Settings not saved: {problem}");
            ShowSaveMessage("⚠ " + problem, failed: true);
            return;
        }

        _log.MinimumLevel = Settings.MinimumLogLevel;
        if (Persist())
        {
            _log.Info("Settings saved");
            ShowSaveMessage("✓ Settings saved", failed: false);
        }
        else
        {
            ShowSaveMessage("⚠ Could not save settings (see log)", failed: true);
        }
    }

    private string? ValidateSettings()
    {
        if (Settings.MaxConcurrentDownloads is < 1 or > 10)
        {
            return "Max concurrent downloads must be 1 to 10.";
        }

        if (Settings.MaxRetryAttempts is < 0 or > 20)
        {
            return "Max retry attempts must be 0 to 20.";
        }

        return Destinations.Validate();
    }

    private void ShowSaveMessage(string message, bool failed)
    {
        SettingsSaveFailed = failed;
        SettingsSaveMessage = message;

        if (Application.Current?.Dispatcher is not { } dispatcher || !dispatcher.CheckAccess())
        {
            return;
        }

        _saveMessageTimer ??= new DispatcherTimer(SaveMessageDuration, DispatcherPriority.Background, (_, _) =>
        {
            _saveMessageTimer!.Stop();
            SettingsSaveMessage = null;
        }, dispatcher);
        _saveMessageTimer.Stop();
        _saveMessageTimer.Start();
    }

    private DownloadItemViewModel AddViewModelFor(DownloadItem item)
    {
        var vm = new DownloadItemViewModel(item, _orchestrator, Rename);
        PlaceInCollection(vm);
        return vm;
    }

    private void PlaceInCollection(DownloadItemViewModel vm)
    {
        var isHistory = vm.Status is DownloadStatus.Completed or DownloadStatus.Canceled;
        var target = isHistory ? History : Queue;
        var other = isHistory ? Queue : History;

        other.Remove(vm);
        if (!target.Contains(vm))
        {
            target.Add(vm);
        }
    }

    private void OnOrchestratorStateChanged()
    {
        void Reconcile()
        {
            foreach (var vm in Queue.Concat(History).ToList())
            {
                vm.RefreshFromModel();
                PlaceInCollection(vm);
            }

            Persist();
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(Reconcile);
        }
        else
        {
            Reconcile();
        }
    }

    private bool Persist() => _store.Save(_state);
}
