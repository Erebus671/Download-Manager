using System.IO;
using DownloadManagerApplet.Models;
using DownloadManagerApplet.Services;
using DotNetTestKit;
using DotNetTestKit.Xunit;

namespace DownloadManagerApplet.Tests;

public class DownloadOrchestratorTests
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void Enqueue_TransitionsToCompleted_WhenEngineSucceeds()
    {
        var orchestrator = new DownloadOrchestrator(new ImmediateSuccessEngine(), NullLoggingService.Instance, () => 2, () => 3);
        var item = NewItem();

        orchestrator.Enqueue(item, new Progress<DownloadProgress>());

        EventuallyAssert.True(() => item.Status == DownloadStatus.Completed, PollTimeout);
    }

    [Fact]
    public void Pause_MovesADownloadingItemToPaused()
    {
        var orchestrator = new DownloadOrchestrator(new NeverCompletingEngine(), NullLoggingService.Instance, () => 2, () => 3);
        var item = NewItem();

        orchestrator.Enqueue(item, new Progress<DownloadProgress>());
        Assert.True(Wait.Until(() => item.Status == DownloadStatus.Downloading, PollTimeout));

        orchestrator.Pause(item);

        EventuallyAssert.True(() => item.Status == DownloadStatus.Paused, PollTimeout);
    }

    [Fact]
    public void Cancel_MovesAQueuedItemToCanceledImmediately()
    {
        // A huge concurrency ceiling of 0 (clamped to 1 internally) combined with an
        // already-occupied slot would be more realistic, but the simplest deterministic
        // way to hit the "not currently running" branch is to cancel before the engine
        // ever starts - which race condition aside, Cancel handles explicitly.
        var orchestrator = new DownloadOrchestrator(new NeverCompletingEngine(), NullLoggingService.Instance, () => 2, () => 3);
        var item = NewItem();

        orchestrator.Cancel(item);

        Assert.Equal(DownloadStatus.Canceled, item.Status);
    }

    [Fact]
    public void Enqueue_TransitionsToError_WhenRetriesAreExhausted()
    {
        var orchestrator = new DownloadOrchestrator(new AlwaysFailingEngine(), NullLoggingService.Instance, () => 2, () => 0);
        var item = NewItem();

        orchestrator.Enqueue(item, new Progress<DownloadProgress>());

        EventuallyAssert.True(() => item.Status == DownloadStatus.Error, PollTimeout);
        Assert.NotNull(item.LastError);
    }

    private static DownloadItem NewItem() => new()
    {
        Url = "https://example.com/file.bin",
        FileName = "file.bin",
        DestinationFolder = Path.GetTempPath()
    };
}
