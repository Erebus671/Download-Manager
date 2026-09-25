using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using DownloadManagerApplet.Models;
using DownloadManagerApplet.Mvvm;
using DownloadManagerApplet.Services;
using Microsoft.Win32;

namespace DownloadManagerApplet.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly IAppStore _store;
    private readonly DownloadOrchestrator _orchestrator;
    private readonly ILoggingService _log;
    private readonly AppState _state;
    private readonly List<Guid> _resumeAfterUpdate = new();

    private static readonly TimeSpan PendingResumeMaxAge = TimeSpan.FromHours(2);
    private static readonly TimeSpan SuspendTimeout = TimeSpan.FromSeconds(15);

    private string _newDownloadUrl = string.Empty;
    private string _newDownloadFolder;

    public ObservableCollection<DownloadItemViewModel> Queue { get; } = new();
    public ObservableCollection<DownloadItemViewModel> History { get; } = new();
    public ReadOnlyObservableCollection<LogEntry> LogEntries => _log.RecentEntries;

    public AppSettings Settings => _state.Settings;
    public UpdatesViewModel? Updates { get; }
    public IReadOnlyList<LogLevelSetting> LogLevels { get; } = Enum.GetValues<LogLevelSetting>();

    public bool HasRestoredPendingDownloads { get; }

    /// <summary>Downloads paused for an update; resumed by <see cref="ResumeAfterUpdate"/> with no prompt.</summary>
    public bool HasDownloadsToResumeAfterUpdate => _resumeAfterUpdate.Count > 0;

    public int ActiveDownloadCount => Queue.Count(v => v.Status is DownloadStatus.Downloading or DownloadStatus.Queued);

    public string NewDownloadUrl
    {
        get => _newDownloadUrl;
        set => SetProperty(ref _newDownloadUrl, value);
    }

    public string NewDownloadFolder
    {
        get => _newDownloadFolder;
        set => SetProperty(ref _newDownloadFolder, value);
    }

    public RelayCommand AddDownloadCommand { get; }
    public RelayCommand BrowseFolderCommand { get; }
    public RelayCommand PauseAllCommand { get; }
    public RelayCommand ResumeAllCommand { get; }
    public RelayCommand ClearCompletedCommand { get; }
    public RelayCommand SaveSettingsCommand { get; }

    public MainViewModel(AppState state, IAppStore store, DownloadOrchestrator orchestrator, ILoggingService log, UpdatesViewModel? updates = null)
    {
        Updates = updates;
        _state = state;
        _store = store;
        _orchestrator = orchestrator;
        _log = log;
        _newDownloadFolder = state.Settings.DefaultDownloadFolder;

        AddDownloadCommand = new RelayCommand(AddDownload);
        BrowseFolderCommand = new RelayCommand(BrowseFolder);
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
        var folder = string.IsNullOrWhiteSpace(NewDownloadFolder) ? Settings.DefaultDownloadFolder : NewDownloadFolder;
        if (TryAddDownload(NewDownloadUrl, folder))
        {
            NewDownloadUrl = string.Empty;
        }
    }

    /// <summary>Queues URLs handed over by another process into the default folder. Returns the number queued.</summary>
    public int AddExternalDownloads(IEnumerable<string> urls)
    {
        var added = 0;
        foreach (var url in urls)
        {
            if (TryAddDownload(url, Settings.DefaultDownloadFolder))
            {
                added++;
            }
        }

        return added;
    }

    private bool TryAddDownload(string rawUrl, string folder)
    {
        var url = rawUrl.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            _log.Warn($"Not a valid http(s) URL: '{url}'");
            return false;
        }

        var fileName = MakeUniqueFileName(folder, DeriveFileName(uri));

        var item = new DownloadItem
        {
            Url = url,
            FileName = fileName,
            DestinationFolder = folder
        };

        _state.Downloads.Add(item);
        var vm = AddViewModelFor(item);
        _orchestrator.Enqueue(item, vm);
        Persist();
        return true;
    }

    private void BrowseFolder()
    {
        var dialog = new OpenFolderDialog
        {
            InitialDirectory = Directory.Exists(NewDownloadFolder) ? NewDownloadFolder : Settings.DefaultDownloadFolder
        };

        if (dialog.ShowDialog() == true)
        {
            NewDownloadFolder = dialog.FolderName;
        }
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
        _log.MinimumLevel = Settings.MinimumLogLevel;
        Persist();
    }

    private DownloadItemViewModel AddViewModelFor(DownloadItem item)
    {
        var vm = new DownloadItemViewModel(item, _orchestrator);
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

    private void Persist() => _store.Save(_state);

    private static string DeriveFileName(Uri uri)
    {
        var name = Path.GetFileName(uri.LocalPath);
        return string.IsNullOrWhiteSpace(name) ? $"download-{DateTime.Now:yyyyMMdd-HHmmss}" : name;
    }

    private string MakeUniqueFileName(string folder, string fileName)
    {
        var candidate = fileName;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var suffix = 1;

        while (File.Exists(Path.Combine(folder, candidate)) || IsNameInUse(folder, candidate))
        {
            candidate = $"{stem} ({suffix}){ext}";
            suffix++;
        }

        return candidate;
    }

    private bool IsNameInUse(string folder, string candidate) =>
        _state.Downloads.Any(d =>
            string.Equals(d.DestinationFolder, folder, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(d.FileName, candidate, StringComparison.OrdinalIgnoreCase));
}
