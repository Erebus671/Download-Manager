using DownloadManagerApplet.Services.Updates;
using DotNetTestKit;

namespace DownloadManagerApplet.Tests;

public class UpdateArgumentsTests : TestBase
{
    [Fact]
    public void AfterUpdateInfo_RoundTripsAndIsRemovedFromArgs()
    {
        var info = new AfterUpdateInfo(UpdateOutcome.Canceled, new Version(1, 1, 0), 1602, null);
        var args = new List<string> { "https://example.com/a.zip" };
        args.AddRange(info.ToArguments());

        var parsed = AfterUpdateInfo.Extract(args);

        Assert.Equal(info, parsed);
        Assert.Equal(["https://example.com/a.zip"], args);
    }

    [Fact]
    public void AfterUpdateInfo_MalformedIsRemovedAndIgnored()
    {
        var args = new List<string> { AfterUpdateInfo.Flag, "Exploded", "1.1.0", "0" };

        Assert.Null(AfterUpdateInfo.Extract(args));
        Assert.Empty(args);
    }

    [Fact]
    public void AfterUpdateInfo_AbsentReturnsNull()
    {
        var args = new List<string> { "https://example.com/a.zip" };

        Assert.Null(AfterUpdateInfo.Extract(args));
        Assert.Single(args);
    }

    [Fact]
    public void ParseRequest_RoundTripsBuildArguments()
    {
        var release = new ReleaseInfo(new Version(1, 1, 0), "v1.1.0", string.Empty, false,
            new ReleaseAsset("a.exe", new Uri("https://example.com/a.exe"), 10),
            new ReleaseAsset("a.exe.sig", new Uri("https://example.com/a.exe.sig"), 1));
        var update = new VerifiedUpdate(release, new SignedUpdateManifest("k", release.Version, "a.exe", 10, new string('0', 64)),
            @"C:\u\1.1.0\a.exe", @"C:\u\1.1.0\a.exe.sig");

        var request = UpdateApplier.ParseRequest(UpdateApplier.BuildArguments(update, 1234, @"C:\app\App.exe", @"C:\logs").ToList());

        Assert.Equal(new UpdateApplier.Request(update.InstallerPath, update.SignaturePath, release.Version, 1234, @"C:\app\App.exe", @"C:\logs"), request);
    }

    [Theory]
    [InlineData("--apply-update")]
    [InlineData("--apply-update --installer")]
    [InlineData("--apply-update --installer x --sig y --version bad --wait-pid 1 --app a --log-dir l")]
    [InlineData("--other --installer x")]
    public void ParseRequest_RejectsIncompleteOrInvalidArgs(string args)
    {
        Assert.Null(UpdateApplier.ParseRequest(args.Split(' ')));
    }
}
