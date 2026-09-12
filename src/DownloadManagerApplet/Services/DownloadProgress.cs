namespace DownloadManagerApplet.Services;

public readonly record struct DownloadProgress(long BytesReceived, long? TotalBytes, double BytesPerSecond);
