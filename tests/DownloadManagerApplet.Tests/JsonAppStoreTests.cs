using System.IO;
using DownloadManagerApplet.Models;
using DownloadManagerApplet.Services;
using DotNetTestKit;

namespace DownloadManagerApplet.Tests;

public class JsonAppStoreTests
{
    [Fact]
    public void SaveThenLoad_RoundTripsSettingsAndDownloads()
    {
        using var temp = new TempDirectory();
        var store = new JsonAppStore(Path.Combine(temp.Path, "state.json"), NullLoggingService.Instance);

        var state = new AppState
        {
            Settings = new AppSettings
            {
                DefaultDownloadFolder = @"C:\Somewhere",
                MaxConcurrentDownloads = 5,
                MaxRetryAttempts = 7,
                MinimumLogLevel = LogLevelSetting.Warn
            },
            Downloads =
            {
                new DownloadItem
                {
                    Url = "https://example.com/file.bin",
                    FileName = "file.bin",
                    DestinationFolder = @"C:\Somewhere",
                    Status = DownloadStatus.Paused,
                    BytesReceived = 12345
                }
            }
        };

        store.Save(state);
        var reloaded = store.Load();

        Assert.Equal(5, reloaded.Settings.MaxConcurrentDownloads);
        Assert.Equal(7, reloaded.Settings.MaxRetryAttempts);
        Assert.Equal(LogLevelSetting.Warn, reloaded.Settings.MinimumLogLevel);
        Assert.Single(reloaded.Downloads);
        Assert.Equal("file.bin", reloaded.Downloads[0].FileName);
        Assert.Equal(DownloadStatus.Paused, reloaded.Downloads[0].Status);
        Assert.Equal(12345, reloaded.Downloads[0].BytesReceived);
    }

    [Fact]
    public void Load_ReturnsFreshState_WhenFileDoesNotExist()
    {
        using var temp = new TempDirectory();
        var store = new JsonAppStore(Path.Combine(temp.Path, "does-not-exist.json"), NullLoggingService.Instance);

        var state = store.Load();

        Assert.Empty(state.Downloads);
    }
}
