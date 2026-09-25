using System.Text;
using DownloadManagerApplet.Services;
using DownloadManagerApplet.Services.Updates;
using DotNetTestKit;

namespace DownloadManagerApplet.Tests;

public class GitHubReleaseSourceTests : TestBase
{
    [Fact]
    public void Parse_PicksHighestStableReleaseWithBothAssets()
    {
        var json = Releases(
            Release("v1.2.0", assets: Assets("1.2.0")),
            Release("v1.3.0", prerelease: true, assets: Assets("1.3.0")),
            Release("v1.4.0", draft: true, assets: Assets("1.4.0")),
            Release("v1.5.0", assets: $"[{Asset(GitHubReleaseSource.InstallerAssetName, "1.5.0")}]"),
            Release("v1.1.0", assets: Assets("1.1.0")));

        var release = GitHubReleaseSource.Parse(json, includePrerelease: false, NullLoggingService.Instance);

        Assert.NotNull(release);
        Assert.Equal(new Version(1, 2, 0), release.Version);
        Assert.Equal("notes for v1.2.0", release.Notes);
        Assert.Equal(GitHubReleaseSource.SignatureAssetName, release.Signature.Name);
    }

    [Fact]
    public void Parse_IncludesPrereleaseWhenAsked()
    {
        var json = Releases(Release("v1.2.0", assets: Assets("1.2.0")), Release("v1.3.0", prerelease: true, assets: Assets("1.3.0")));

        var release = GitHubReleaseSource.Parse(json, includePrerelease: true, NullLoggingService.Instance);

        Assert.Equal(new Version(1, 3, 0), release!.Version);
        Assert.True(release.IsPrerelease);
    }

    [Fact]
    public void Parse_IgnoresReleaseWithNonHttpsAsset()
    {
        var assets = $"[{Asset(GitHubReleaseSource.InstallerAssetName, "1.2.0", scheme: "http")},{Asset(GitHubReleaseSource.SignatureAssetName, "1.2.0")}]";

        Assert.Null(GitHubReleaseSource.Parse(Releases(Release("v1.2.0", assets: assets)), false, NullLoggingService.Instance));
    }

    [Fact]
    public void Parse_ReturnsNullForEmptyList()
    {
        Assert.Null(GitHubReleaseSource.Parse(Encoding.UTF8.GetBytes("[]"), false, NullLoggingService.Instance));
    }

    [Theory]
    [InlineData("v1.2.3", true)]
    [InlineData("1.2.3", true)]
    [InlineData("v1.2", false)]
    [InlineData("v1.2.3.4", false)]
    [InlineData("v1.2.3-beta", false)]
    [InlineData("latest", false)]
    public void TryParseTag_AcceptsOnlyMajorMinorPatch(string tag, bool expected)
    {
        Assert.Equal(expected, GitHubReleaseSource.TryParseTag(tag, out _));
    }

    private static byte[] Releases(params string[] releases) => Encoding.UTF8.GetBytes($"[{string.Join(",", releases)}]");

    private static string Release(string tag, bool draft = false, bool prerelease = false, string assets = "[]") =>
        $"{{\"tag_name\":\"{tag}\",\"draft\":{Lower(draft)},\"prerelease\":{Lower(prerelease)},\"body\":\"notes for {tag}\",\"assets\":{assets}}}";

    private static string Assets(string version) =>
        $"[{Asset(GitHubReleaseSource.InstallerAssetName, version)},{Asset(GitHubReleaseSource.SignatureAssetName, version)},{Asset("readme.txt", version)}]";

    private static string Asset(string name, string version, string scheme = "https") =>
        $"{{\"name\":\"{name}\",\"size\":100,\"browser_download_url\":\"{scheme}://example.com/v{version}/{name}\"}}";

    private static string Lower(bool b) => b ? "true" : "false";
}
