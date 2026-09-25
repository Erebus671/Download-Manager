using System.IO;
using DownloadManagerApplet.Models;
using DownloadManagerApplet.Services;
using DownloadManagerApplet.ViewModels;
using DotNetTestKit;

namespace DownloadManagerApplet.Tests;

public class UpdateResumeTests : TestBase
{
    [Theory]
    [InlineData(DownloadStatus.Downloading)]
    [InlineData(DownloadStatus.Queued)]
    public void Startup_RestoresUnfinishedDownloadsAsPaused(DownloadStatus status)
    {
        var state = NewState();
        var item = AddItem(state, status);

        var vm = NewViewModel(state, new ImmediateSuccessEngine());

        Assert.Equal(DownloadStatus.Paused, item.Status);
        Assert.True(vm.HasRestoredPendingDownloads);
        Assert.False(vm.HasDownloadsToResumeAfterUpdate);
    }

    [Fact]
    public void Startup_RecentPendingResume_ResumesWithoutPrompt()
    {
        var state = NewState();
        var item = AddItem(state, DownloadStatus.Paused);
        state.Updates.PendingResume = new PendingUpdateResume { DownloadIds = [item.Id], CreatedAt = DateTimeOffset.Now.AddMinutes(-3), TargetVersion = "1.2.0" };

        var vm = NewViewModel(state, new ImmediateSuccessEngine());

        Assert.True(vm.HasDownloadsToResumeAfterUpdate);
        Assert.False(vm.HasRestoredPendingDownloads);
        Assert.Null(state.Updates.PendingResume);

        vm.ResumeAfterUpdate();
        Assert.False(vm.HasDownloadsToResumeAfterUpdate);
        Assert.NotEqual(DownloadStatus.Paused, item.Status);
    }

    [Fact]
    public void Startup_StalePendingResume_FallsBackToPrompt()
    {
        var state = NewState();
        var item = AddItem(state, DownloadStatus.Paused);
        state.Updates.PendingResume = new PendingUpdateResume { DownloadIds = [item.Id], CreatedAt = DateTimeOffset.Now.AddHours(-3), TargetVersion = "1.2.0" };

        var vm = NewViewModel(state, new ImmediateSuccessEngine());

        Assert.False(vm.HasDownloadsToResumeAfterUpdate);
        Assert.True(vm.HasRestoredPendingDownloads);
        Assert.Null(state.Updates.PendingResume);
    }

    [Fact]
    public async Task PrepareForUpdate_PausesActiveDownloadsAndRecordsThem()
    {
        var state = NewState();
        var vm = NewViewModel(state, new NeverCompletingEngine());
        vm.AddExternalDownloads(["https://example.com/a.zip", "https://example.com/b.zip", "https://example.com/c.zip"]);

        Assert.True(await vm.PrepareForUpdateAsync(new Version(1, 2, 0)));

        Assert.All(state.Downloads, d => Assert.Equal(DownloadStatus.Paused, d.Status));
        var pending = state.Updates.PendingResume;
        Assert.NotNull(pending);
        Assert.Equal(state.Downloads.Select(d => d.Id).Order(), pending.DownloadIds.Order());
        Assert.Equal("1.2.0", pending.TargetVersion);
    }

    [Fact]
    public async Task CancelUpdatePreparation_ResumesDownloads()
    {
        var state = NewState();
        var vm = NewViewModel(state, new NeverCompletingEngine());
        vm.AddExternalDownloads(["https://example.com/a.zip"]);
        await vm.PrepareForUpdateAsync(new Version(1, 2, 0));

        vm.CancelUpdatePreparation();

        Assert.Null(state.Updates.PendingResume);
        Assert.NotEqual(DownloadStatus.Paused, state.Downloads.Single().Status);
    }

    private AppState NewState()
    {
        var state = TestStates.InFolder(Temp.Path);
        return state;
    }

    private DownloadItem AddItem(AppState state, DownloadStatus status)
    {
        var item = new DownloadItem { Url = "https://example.com/x.zip", FileName = "x.zip", DestinationFolder = Temp.Path, Status = status };
        state.Downloads.Add(item);
        return item;
    }

    private MainViewModel NewViewModel(AppState state, IDownloadEngine engine)
    {
        var store = new JsonAppStore(Path.Combine(Temp.Path, "state.json"), NullLoggingService.Instance);
        var orchestrator = new DownloadOrchestrator(engine, NullLoggingService.Instance, () => 2, () => 3);
        return new MainViewModel(state, store, orchestrator, NullLoggingService.Instance);
    }
}
