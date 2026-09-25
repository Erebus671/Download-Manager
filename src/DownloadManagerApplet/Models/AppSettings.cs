using System.IO;

namespace DownloadManagerApplet.Models;

public enum LogLevelSetting
{
    Debug,
    Info,
    Warn,
    Error,
    Critical,
    Fatal
}

public enum UpdateCheckFrequency
{
    LaunchAndDaily,
    LaunchOnly,
    Every6Hours,
    Weekly
}

public sealed class AppSettings
{
    public string DefaultDownloadFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public int MaxConcurrentDownloads { get; set; } = 2;
    public int MaxRetryAttempts { get; set; } = 3;
    public LogLevelSetting MinimumLogLevel { get; set; } = LogLevelSetting.Info;

    public bool CheckForUpdatesAutomatically { get; set; } = true;
    public UpdateCheckFrequency UpdateCheckFrequency { get; set; } = UpdateCheckFrequency.LaunchAndDaily;
    public bool DownloadUpdatesAutomatically { get; set; } = true;

    /// <summary>Not shown in the UI; set in state.json to test releases marked pre-release on GitHub.</summary>
    public bool IncludePrereleaseUpdates { get; set; }
}
