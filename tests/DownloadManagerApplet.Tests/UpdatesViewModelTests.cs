using DownloadManagerApplet.Services;
using DownloadManagerApplet.Services.Updates;
using DownloadManagerApplet.ViewModels;
using DotNetTestKit;

namespace DownloadManagerApplet.Tests;

public class UpdatesViewModelTests : TestBase
{
    private static readonly Version Current = new(1, 1, 0);

    [Fact]
    public async Task Check_UpToDate_WhenLatestIsNotNewer()
    {
        var (vm, source, downloader, _) = NewViewModel(latest: Current);

        await vm.CheckAsync(manual: false);

        Assert.Equal(UpdateStatus.UpToDate, vm.Status);
        Assert.True(string.IsNullOrEmpty(vm.StatusBarText));
        Assert.Equal(0, downloader.Downloads);
        Assert.NotEqual("Never", vm.LastCheckedDisplay);
    }

    [Fact]
    public async Task Check_NewerRelease_DownloadsAndPrompts()
    {
        var (vm, _, downloader, _) = NewViewModel(latest: new Version(1, 2, 0));
        VerifiedUpdate? prompted = null;
        vm.PromptRequested += u => prompted = u;

        await vm.CheckAsync(manual: false);

        Assert.Equal(UpdateStatus.Ready, vm.Status);
        Assert.Equal(1, downloader.Downloads);
        Assert.NotNull(prompted);
        Assert.True(vm.StatusBarIsClickable);
    }

    [Fact]
    public async Task Check_AutoDownloadOff_OnlyAnnounces()
    {
        var (vm, _, downloader, state) = NewViewModel(latest: new Version(1, 2, 0));
        state.Settings.DownloadUpdatesAutomatically = false;

        await vm.CheckAsync(manual: false);

        Assert.Equal(UpdateStatus.Available, vm.Status);
        Assert.Equal(0, downloader.Downloads);
        Assert.Contains("click to download", vm.StatusBarText);
    }

    [Fact]
    public async Task Skip_SuppressesAutomaticChecksButNotManual()
    {
        var (vm, _, downloader, state) = NewViewModel(latest: new Version(1, 2, 0));
        VerifiedUpdate? prompted = null;
        vm.PromptRequested += u => prompted = u;
        await vm.CheckAsync(manual: false);

        vm.OnPromptResult(prompted!, UpdatePromptResult.Skip);
        Assert.Equal("1.2.0", state.Updates.SkippedVersion);

        prompted = null;
        await vm.CheckAsync(manual: false);
        Assert.Null(prompted);
        Assert.Equal(1, downloader.Downloads);

        await vm.CheckAsync(manual: true);
        Assert.NotNull(prompted);
    }

    [Fact]
    public async Task Later_DefersPromptUntilManualCheck()
    {
        var (vm, _, _, _) = NewViewModel(latest: new Version(1, 2, 0));
        var prompts = 0;
        VerifiedUpdate? last = null;
        vm.PromptRequested += u => { prompts++; last = u; };
        await vm.CheckAsync(manual: false);

        vm.OnPromptResult(last!, UpdatePromptResult.Later);
        await vm.CheckAsync(manual: false);
        Assert.Equal(1, prompts);

        await vm.CheckAsync(manual: true);
        Assert.Equal(2, prompts);
    }

    [Fact]
    public async Task SignatureFailure_ShowsErrorInStatusBar()
    {
        var (vm, _, downloader, _) = NewViewModel(latest: new Version(1, 2, 0));
        downloader.Failure = new UpdateSignatureException("bad");

        await vm.CheckAsync(manual: false);

        Assert.Equal(UpdateStatus.Failed, vm.Status);
        Assert.True(vm.StatusBarIsError);
        Assert.Contains("signature", vm.StatusBarText);
    }

    [Fact]
    public async Task CheckFailure_IsReportedWithoutThrowing()
    {
        var (vm, source, _, state) = NewViewModel(latest: null);
        source.Failure = new UpdateCheckException("offline");

        await vm.CheckAsync(manual: true);

        Assert.Equal(UpdateStatus.Failed, vm.Status);
        Assert.Contains("offline", vm.StatusSummary);
        Assert.Null(state.Updates.LastCheck);
    }

    [Fact]
    public async Task Install_WhenHandlerFails_ReturnsToReadyWithRetry()
    {
        var (vm, _, _, _) = NewViewModel(latest: new Version(1, 2, 0));
        VerifiedUpdate? prompted = null;
        vm.PromptRequested += u => prompted = u;
        vm.InstallHandler = _ => Task.FromException<bool>(new UpdateCheckException("downloads did not pause"));
        await vm.CheckAsync(manual: false);

        vm.OnPromptResult(prompted!, UpdatePromptResult.Install);

        Assert.Equal(UpdateStatus.Ready, vm.Status);
        Assert.True(vm.StatusBarIsError);
        Assert.Contains("retry", vm.StatusBarText);
    }

    [Fact]
    public void Disable_BlocksManualCheck()
    {
        var (vm, _, _, _) = NewViewModel(latest: new Version(1, 2, 0));

        vm.Disable("Updates disabled in this build");

        Assert.False(vm.CheckNowCommand.CanExecute(null));
        Assert.Equal("Updates disabled in this build", vm.StatusSummary);
    }

    private static (UpdatesViewModel, FakeReleaseSource, FakeUpdateDownloader, AppState) NewViewModel(Version? latest)
    {
        var state = new AppState();
        var source = new FakeReleaseSource { Latest = latest is null ? null : NewRelease(latest) };
        var downloader = new FakeUpdateDownloader();
        var vm = new UpdatesViewModel(state, () => { }, source, downloader, Current, NullLoggingService.Instance, TimeProvider.System);
        return (vm, source, downloader, state);
    }

    private static ReleaseInfo NewRelease(Version v) => new(
        v, $"v{v.ToString(3)}", "notes", false,
        new ReleaseAsset(GitHubReleaseSource.InstallerAssetName, new Uri("https://example.com/i.exe"), 10),
        new ReleaseAsset(GitHubReleaseSource.SignatureAssetName, new Uri("https://example.com/i.exe.sig"), 1));

    private sealed class FakeReleaseSource : IReleaseSource
    {
        public ReleaseInfo? Latest { get; set; }
        public Exception? Failure { get; set; }

        public Task<ReleaseInfo?> GetLatestAsync(bool includePrerelease, CancellationToken cancellationToken) =>
            Failure is null ? Task.FromResult(Latest) : Task.FromException<ReleaseInfo?>(Failure);
    }

    private sealed class FakeUpdateDownloader : IUpdateDownloader
    {
        public int Downloads { get; private set; }
        public Exception? Failure { get; set; }

        public Task<VerifiedUpdate> DownloadAsync(ReleaseInfo release, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            Downloads++;
            if (Failure is not null)
            {
                return Task.FromException<VerifiedUpdate>(Failure);
            }

            var manifest = new SignedUpdateManifest("k", release.Version, release.Installer.Name, release.Installer.Size, new string('0', 64));
            return Task.FromResult(new VerifiedUpdate(release, manifest, "installer.exe", "installer.exe.sig"));
        }

        public void CleanUp(Version? keep)
        {
        }
    }
}
