using System.Collections.ObjectModel;
using DownloadManagerApplet.Models;

namespace DownloadManagerApplet.Services;

/// <summary>No-op logger used only during the brief bootstrap window before the real logger exists.</summary>
public sealed class NullLoggingService : ILoggingService
{
    public static readonly NullLoggingService Instance = new();

    private readonly ReadOnlyObservableCollection<LogEntry> _empty = new(new ObservableCollection<LogEntry>());

    public ReadOnlyObservableCollection<LogEntry> RecentEntries => _empty;
    public LogLevelSetting MinimumLevel { get; set; } = LogLevelSetting.Info;

    public void Debug(string message) { }
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message, Exception? exception = null) { }
}
