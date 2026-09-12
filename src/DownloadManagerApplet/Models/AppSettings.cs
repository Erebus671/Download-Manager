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

public sealed class AppSettings
{
    public string DefaultDownloadFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Movies");

    public int MaxConcurrentDownloads { get; set; } = 2;
    public int MaxRetryAttempts { get; set; } = 3;
    public LogLevelSetting MinimumLogLevel { get; set; } = LogLevelSetting.Info;
}
