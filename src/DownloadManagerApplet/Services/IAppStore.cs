using DownloadManagerApplet.Models;

namespace DownloadManagerApplet.Services;

public sealed class AppState
{
    public AppSettings Settings { get; set; } = new();
    public List<DownloadItem> Downloads { get; set; } = new();
}

public interface IAppStore
{
    AppState Load();
    void Save(AppState state);
}
