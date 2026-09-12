using System.Collections.ObjectModel;
using DownloadManagerApplet.Models;

namespace DownloadManagerApplet.Services;

public sealed record LogEntry(DateTimeOffset Timestamp, LogLevelSetting Level, string Message);

public interface ILoggingService
{
    /// <summary>Recent log entries for display in the UI log panel (bounded ring buffer).</summary>
    ReadOnlyObservableCollection<LogEntry> RecentEntries { get; }

    LogLevelSetting MinimumLevel { get; set; }

    void Debug(string message);
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? exception = null);
}
