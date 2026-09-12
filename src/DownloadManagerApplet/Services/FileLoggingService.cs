using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using DownloadManagerApplet.Models;

namespace DownloadManagerApplet.Services;

/// <summary>
/// Hand-rolled leveled logger: writes to a daily rolling text file and keeps a bounded
/// in-memory buffer for the UI log panel. No Serilog dependency (see csproj comment).
/// </summary>
public sealed class FileLoggingService : ILoggingService, IDisposable
{
    private const int MaxRecentEntries = 500;
    private const int RetainedDays = 14;

    private readonly string _logDirectory;
    private readonly object _fileLock = new();
    private readonly ObservableCollection<LogEntry> _recentEntries = new();

    public ReadOnlyObservableCollection<LogEntry> RecentEntries { get; }

    public LogLevelSetting MinimumLevel { get; set; }

    public FileLoggingService(string logDirectory, LogLevelSetting initialLevel)
    {
        _logDirectory = logDirectory;
        MinimumLevel = initialLevel;
        Directory.CreateDirectory(_logDirectory);
        PruneOldLogs();

        RecentEntries = new ReadOnlyObservableCollection<LogEntry>(_recentEntries);
    }

    public void Debug(string message) => Write(LogLevelSetting.Debug, message, null);
    public void Info(string message) => Write(LogLevelSetting.Info, message, null);
    public void Warn(string message) => Write(LogLevelSetting.Warn, message, null);
    public void Error(string message, Exception? exception = null) => Write(LogLevelSetting.Error, message, exception);

    private void Write(LogLevelSetting level, string message, Exception? exception)
    {
        if (level < MinimumLevel)
        {
            return;
        }

        var timestamp = DateTimeOffset.Now;
        var fullMessage = exception is null ? message : $"{message}: {exception}";
        var line = $"[{timestamp:yyyy-MM-dd HH:mm:ss.fff}] {level,-8} {fullMessage}";

        AppendToFile(timestamp, line);
        AppendToRecent(new LogEntry(timestamp, level, exception is null ? message : $"{message}: {exception.Message}"));
    }

    private void AppendToFile(DateTimeOffset timestamp, string line)
    {
        var path = Path.Combine(_logDirectory, $"log-{timestamp:yyyyMMdd}.txt");
        lock (_fileLock)
        {
            try
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch (IOException)
            {
                // Best-effort: a locked or unwritable log file must not crash the app.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void PruneOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-RetainedDays);
            foreach (var file in Directory.EnumerateFiles(_logDirectory, "log-*.txt"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void AppendToRecent(LogEntry entry)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            AppendCore(entry);
        }
        else
        {
            dispatcher.BeginInvoke(() => AppendCore(entry));
        }
    }

    private void AppendCore(LogEntry entry)
    {
        _recentEntries.Add(entry);
        while (_recentEntries.Count > MaxRecentEntries)
        {
            _recentEntries.RemoveAt(0);
        }
    }

    public void Dispose()
    {
        // No unmanaged resources; file writes are opened/closed per call.
    }
}
