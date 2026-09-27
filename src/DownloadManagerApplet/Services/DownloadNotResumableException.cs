namespace DownloadManagerApplet.Services;

/// <summary>A download that retrying can't fix; the orchestrator fails it without retries.</summary>
public sealed class DownloadNotResumableException(string message) : Exception(message);
