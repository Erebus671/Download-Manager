using System.Windows;
using DownloadManagerApplet.Models;
using DownloadManagerApplet.Mvvm;
using DownloadManagerApplet.Services;

namespace DownloadManagerApplet.ViewModels;

public sealed class DownloadItemViewModel : ObservableObject, IProgress<DownloadProgress>
{
    private readonly DownloadOrchestrator _orchestrator;

    private DownloadStatus _status;
    private double _progressPercent;
    private string _receivedDisplay = string.Empty;
    private string _speedDisplay = string.Empty;
    private string? _lastError;

    public DownloadItem Model { get; }

    public DownloadStatus Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                PauseCommand.NotifyCanExecuteChanged();
                ResumeCommand.NotifyCanExecuteChanged();
                CancelCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        private set => SetProperty(ref _progressPercent, value);
    }

    public string ReceivedDisplay
    {
        get => _receivedDisplay;
        private set => SetProperty(ref _receivedDisplay, value);
    }

    public string SpeedDisplay
    {
        get => _speedDisplay;
        private set => SetProperty(ref _speedDisplay, value);
    }

    public string? LastError
    {
        get => _lastError;
        private set => SetProperty(ref _lastError, value);
    }

    public string FileName => Model.FileName;
    public string Url => Model.Url;

    public RelayCommand PauseCommand { get; }
    public RelayCommand ResumeCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand CopyUrlCommand { get; }

    public DownloadItemViewModel(DownloadItem model, DownloadOrchestrator orchestrator)
    {
        Model = model;
        _orchestrator = orchestrator;

        PauseCommand = new RelayCommand(() => _orchestrator.Pause(Model), CanPause);
        ResumeCommand = new RelayCommand(() => _orchestrator.Resume(Model), CanResume);
        CancelCommand = new RelayCommand(() => _orchestrator.Cancel(Model), CanCancel);
        CopyUrlCommand = new RelayCommand(() => Clipboard.SetText(Model.Url));

        RefreshFromModel();
    }

    public void RefreshFromModel()
    {
        RunOnUiThread(() =>
        {
            Status = Model.Status;
            LastError = Model.LastError;
            ProgressPercent = ComputePercent(Model.BytesReceived, Model.TotalBytes);
            ReceivedDisplay = FormatReceived(Model.BytesReceived, Model.TotalBytes);
            if (Status is DownloadStatus.Completed or DownloadStatus.Canceled or DownloadStatus.Error)
            {
                SpeedDisplay = string.Empty;
            }
        });
    }

    public void Report(DownloadProgress value)
    {
        RunOnUiThread(() =>
        {
            ProgressPercent = ComputePercent(value.BytesReceived, value.TotalBytes);
            ReceivedDisplay = FormatReceived(value.BytesReceived, value.TotalBytes);
            SpeedDisplay = FormatSpeed(value.BytesPerSecond, value.BytesReceived, value.TotalBytes);
        });
    }

    private static void RunOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }

    private static double ComputePercent(long received, long? total) =>
        total is > 0 ? Math.Clamp(received * 100.0 / total.Value, 0, 100) : 0;

    private static string FormatReceived(long received, long? total) =>
        total is > 0
            ? $"{FormatBytes(received)} / {FormatBytes(total.Value)}"
            : FormatBytes(received);

    private static string FormatSpeed(double bytesPerSecond, long received, long? total)
    {
        if (bytesPerSecond <= 0)
        {
            return string.Empty;
        }

        var speedText = $"{FormatBytes((long)bytesPerSecond)}/s";
        if (total is > 0)
        {
            var remainingSeconds = (total.Value - received) / bytesPerSecond;
            if (remainingSeconds >= 0)
            {
                return $"{speedText} · ETA {FormatDuration(remainingSeconds)}";
            }
        }

        return speedText;
    }

    private static string FormatDuration(double seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h {span.Minutes}m"
            : span.TotalMinutes >= 1
                ? $"{(int)span.TotalMinutes}m {span.Seconds}s"
                : $"{span.Seconds}s";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{value:0} {units[unitIndex]}" : $"{value:0.0} {units[unitIndex]}";
    }

    private bool CanPause() => Status == DownloadStatus.Downloading;
    private bool CanResume() => Status is DownloadStatus.Paused or DownloadStatus.Error or DownloadStatus.Canceled;
    private bool CanCancel() => Status is DownloadStatus.Downloading or DownloadStatus.Queued or DownloadStatus.Paused;
}
