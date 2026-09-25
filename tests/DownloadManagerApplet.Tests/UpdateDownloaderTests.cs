using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using DownloadManagerApplet.Services;
using DownloadManagerApplet.Services.Updates;
using DotNetTestKit;

namespace DownloadManagerApplet.Tests;

public class UpdateDownloaderTests : TestBase
{
    private static readonly byte[] Installer = Encoding.UTF8.GetBytes("pretend installer bytes for the downloader");

    [Fact]
    public async Task DownloadAsync_SavesVerifiedInstaller()
    {
        using var signer = new UpdateTestSigner();
        var (downloader, release, _) = Setup(signer, signer.Sign(Installer, "1.1.0"), Installer);

        var update = await downloader.DownloadAsync(release, null, CancellationToken.None);

        Assert.Equal(Installer, File.ReadAllBytes(update.InstallerPath));
        Assert.True(File.Exists(update.SignaturePath));
    }

    [Fact]
    public async Task DownloadAsync_ReusesCachedDownloadWithoutFetchingInstallerAgain()
    {
        using var signer = new UpdateTestSigner();
        var (downloader, release, handler) = Setup(signer, signer.Sign(Installer, "1.1.0"), Installer);

        await downloader.DownloadAsync(release, null, CancellationToken.None);
        var requests = handler.Requests;
        await downloader.DownloadAsync(release, null, CancellationToken.None);

        Assert.Equal(requests, handler.Requests);
    }

    [Fact]
    public async Task DownloadAsync_DeletesInstallerThatDoesNotMatchSignature()
    {
        using var signer = new UpdateTestSigner();
        var served = (byte[])Installer.Clone();
        served[^1] ^= 0xFF;
        var (downloader, release, _) = Setup(signer, signer.Sign(Installer, "1.1.0"), served);

        await Assert.ThrowsAsync<UpdateSignatureException>(() => downloader.DownloadAsync(release, null, CancellationToken.None));

        Assert.False(Directory.Exists(Path.Combine(Temp.Path, "1.1.0")));
    }

    [Fact]
    public async Task DownloadAsync_RejectsUntrustedSignatureBeforeFetchingInstaller()
    {
        using var signer = new UpdateTestSigner();
        using var attacker = new UpdateTestSigner();
        var (downloader, release, handler) = Setup(signer, attacker.Sign(Installer, "1.1.0"), Installer);

        await Assert.ThrowsAsync<UpdateSignatureException>(() => downloader.DownloadAsync(release, null, CancellationToken.None));

        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task DownloadAsync_FailsClosedWithoutTrustedKeys()
    {
        using var http = new HttpClient(new MapHandler());
        var downloader = new UpdateDownloader(http, new UpdateSignatureVerifier([]), Temp.Path, NullLoggingService.Instance);

        await Assert.ThrowsAsync<UpdateSignatureException>(() => downloader.DownloadAsync(NewRelease(Installer.Length), null, CancellationToken.None));
    }

    [Fact]
    public void CleanUp_KeepsOnlyRequestedVersion()
    {
        Directory.CreateDirectory(Path.Combine(Temp.Path, "1.0.5"));
        Directory.CreateDirectory(Path.Combine(Temp.Path, "1.1.0"));
        Directory.CreateDirectory(Path.Combine(Temp.Path, UpdateLauncher.HelperFolderName));
        using var http = new HttpClient(new MapHandler());
        var downloader = new UpdateDownloader(http, new UpdateSignatureVerifier([]), Temp.Path, NullLoggingService.Instance);

        downloader.CleanUp(new Version(1, 1, 0));

        Assert.Equal(["1.1.0"], Directory.GetDirectories(Temp.Path).Select(Path.GetFileName));
    }

    private (UpdateDownloader, ReleaseInfo, MapHandler) Setup(UpdateTestSigner signer, byte[] sig, byte[] installer)
    {
        var release = NewRelease(Installer.Length);
        var handler = new MapHandler
        {
            [release.Signature.DownloadUrl] = sig,
            [release.Installer.DownloadUrl] = installer
        };
        var http = new HttpClient(handler);
        return (new UpdateDownloader(http, signer.Verifier(), Temp.Path, NullLoggingService.Instance), release, handler);
    }

    private static ReleaseInfo NewRelease(long installerSize) => new(
        new Version(1, 1, 0), "v1.1.0", string.Empty, false,
        new ReleaseAsset(GitHubReleaseSource.InstallerAssetName, new Uri("https://example.com/i.exe"), installerSize),
        new ReleaseAsset(GitHubReleaseSource.SignatureAssetName, new Uri("https://example.com/i.exe.sig"), 300));

    /// <summary>Serves fixed bytes per URL; 404 for anything else.</summary>
    private sealed class MapHandler : HttpMessageHandler
    {
        private readonly Dictionary<Uri, byte[]> _map = new();

        public int Requests { get; private set; }

        public byte[] this[Uri url] { set => _map[url] = value; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(_map.TryGetValue(request.RequestUri!, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
