using DownloadManagerApplet.Models;

namespace DownloadManagerApplet.Services;

public interface IDownloadEngine
{
    /// <summary>
    /// Downloads (or resumes) a single item. Writes to a .part file and renames it to the
    /// final name on success. Throws OperationCanceledException on pause/cancel and
    /// HttpRequestException/IOException on transient failures.
    /// </summary>
    Task DownloadAsync(DownloadItem item, IProgress<DownloadProgress> progress, CancellationToken cancellationToken);
}
