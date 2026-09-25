using DownloadManagerApplet.Models;

namespace DownloadManagerApplet.Services;

public sealed class AppState
{
    public AppSettings Settings { get; set; } = new();
    public List<DownloadItem> Downloads { get; set; } = new();
    public UpdateState Updates { get; set; } = new();
}

public sealed class UpdateState
{
    public DateTimeOffset? LastCheck { get; set; }
    public string? SkippedVersion { get; set; }
    public PendingUpdateResume? PendingResume { get; set; }
}

/// <summary>Downloads paused for an update install; resumed without a prompt on the next launch.</summary>
public sealed class PendingUpdateResume
{
    public List<Guid> DownloadIds { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; }
    public string TargetVersion { get; set; } = string.Empty;
}

public interface IAppStore
{
    AppState Load();
    void Save(AppState state);
}
