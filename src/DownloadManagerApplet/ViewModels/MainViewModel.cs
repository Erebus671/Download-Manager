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

    private string _newDownloadUrl = string.Empty;
    private string _newDownloadFolder;

    public ObservableCollection<DownloadItemViewModel> Queue { get; } = new();
    public ObservableCollection<DownloadItemViewModel> History { get; } = new();
    public ReadOnlyObservableCollection<LogEntry> LogEntries => _log.RecentEntries;

    public AppSettings Settings => _state.Settings;
    public IReadOnlyList<LogLevelSetting> LogLevels { get; } = Enum.GetValues<LogLevelSetting>();

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

    public MainViewModel(AppState state, IAppStore store, DownloadOrchestrator orchestrator, ILoggingService log)
    {
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
            // A prior run may have been closed mid-download; there is no live task to resume it,
            // so surface it as paused rather than a stuck "Downloading" state.
            if (item.Status == DownloadStatus.Downloading)
            {
                item.Status = DownloadStatus.Paused;
            }

            AddViewModelFor(item);
        }

        _orchestrator.StateChanged += OnOrchestratorStateChanged;
    }

    private void AddDownload()
    {
        var url = NewDownloadUrl.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            _log.Warn($"Not a valid http(s) URL: '{url}'");
            return;
        }

        var folder = string.IsNullOrWhiteSpace(NewDownloadFolder) ? Settings.DefaultDownloadFolder : NewDownloadFolder;
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

        NewDownloadUrl = string.Empty;
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
