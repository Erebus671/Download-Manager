using System.IO;

namespace DownloadManagerApplet.Models;

public sealed class DownloadItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Url { get; init; }
    public required string FileName { get; init; }
    public required string DestinationFolder { get; init; }

    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;
    public long BytesReceived { get; set; }
    public long? TotalBytes { get; set; }
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? CompletedAt { get; set; }

    public string FullPath => Path.Combine(DestinationFolder, FileName);
    public string PartFilePath => FullPath + ".part";
}
