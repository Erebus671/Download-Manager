using System.IO;

namespace DownloadManagerApplet.Models;

public sealed class DownloadItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Url { get; init; }
    public required string FileName { get; set; }
    public required string DestinationFolder { get; set; }

    /// <summary>True until the server's response has been seen; the name (and, for <see cref="AutoFolder"/>, the folder) may still change.</summary>
    public bool ResolveOnResponse { get; set; }

    /// <summary>The folder was picked by rules or file type ("Automatic"), not chosen by the user.</summary>
    public bool AutoFolder { get; set; }

    public FileCategory? Category { get; set; }
    public string? RuleName { get; set; }
    public string? MimeType { get; set; }
    public DownloadSource Source { get; set; } = DownloadSource.Manual;

    /// <summary>The user picked the name; the server's name is not adopted.</summary>
    public bool UserNamed { get; set; }

    /// <summary>Set when an unfinished download is renamed; the .part keeps its original name until completion.</summary>
    public string? PartFileName { get; set; }

    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;
    public long BytesReceived { get; set; }
    public long? TotalBytes { get; set; }
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? CompletedAt { get; set; }

    public string FullPath => Path.Combine(DestinationFolder, FileName);
    public string PartFilePath => Path.Combine(DestinationFolder, PartFileName ?? FileName + ".part");
}
