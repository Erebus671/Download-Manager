using System.IO;
using DownloadManagerApplet.Models;
using DownloadManagerApplet.Services;

namespace DownloadManagerApplet.Tests;

/// <summary>Completes immediately and marks the item Completed, like a real engine would on success.</summary>
internal sealed class ImmediateSuccessEngine : IDownloadEngine
{
    public Task DownloadAsync(DownloadItem item, IProgress<DownloadProgress> progress, CancellationToken cancellationToken)
    {
        item.Status = DownloadStatus.Completed;
        return Task.CompletedTask;
    }
}

/// <summary>Never completes on its own; only ends via cancellation, like an in-flight network download.</summary>
internal sealed class NeverCompletingEngine : IDownloadEngine
{
    public Task DownloadAsync(DownloadItem item, IProgress<DownloadProgress> progress, CancellationToken cancellationToken) =>
        Task.Delay(Timeout.Infinite, cancellationToken);
}

/// <summary>Always fails with a transient-looking error, to exercise the retry/give-up path.</summary>
internal sealed class AlwaysFailingEngine : IDownloadEngine
{
    public Task DownloadAsync(DownloadItem item, IProgress<DownloadProgress> progress, CancellationToken cancellationToken) =>
        throw new IOException("simulated transient failure");
}
