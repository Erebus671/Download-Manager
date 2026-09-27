using System.IO;
using DownloadManagerApplet.Models;
using DownloadManagerApplet.Services;
using DownloadManagerApplet.Services.Browser;
using DotNetTestKit;

namespace DownloadManagerApplet.Tests;

public class BrowserPolicyTests : TestBase
{
    private static BrowserIntegrationSettings Enabled(params string[] excluded) => new()
    {
        Enabled = true,
        MinimumBytes = 10 * 1024 * 1024,
        ExcludedSites = excluded.ToList()
    };

    private static BrowserHandoff Handoff(string url = "https://files.example.com/big.iso", long? size = null) => new()
    {
        Url = url,
        TotalBytes = size
    };

    [Fact]
    public void Evaluate_Disabled_WinsOverEverything()
    {
        var settings = Enabled();
        settings.Enabled = false;

        Assert.Equal(BrowserRejectReason.Disabled, BrowserHandoffPolicy.Evaluate(settings, Handoff()));
    }

    [Fact]
    public void Evaluate_PrivateWindow_StaysInBrowser()
    {
        var handoff = Handoff();
        handoff.Incognito = true;

        Assert.Equal(BrowserRejectReason.PrivateWindow, BrowserHandoffPolicy.Evaluate(Enabled(), handoff));
    }

    [Theory]
    [InlineData("blob:https://example.com/123")]
    [InlineData("data:text/plain,hi")]
    [InlineData("file:///C:/x.iso")]
    [InlineData("ftp://example.com/x.iso")]
    public void Evaluate_NonHttpUrl_Unsupported(string url)
    {
        Assert.Equal(BrowserRejectReason.UnsupportedUrl, BrowserHandoffPolicy.Evaluate(Enabled(), Handoff(url)));
    }

    [Fact]
    public void Evaluate_ExcludedByReferrerHost()
    {
        var handoff = Handoff("https://cdn.example.net/file.zip");
        handoff.Referrer = "https://drive.google.com/view";

        Assert.Equal(BrowserRejectReason.SiteExcluded, BrowserHandoffPolicy.Evaluate(Enabled("google.com"), handoff));
    }

    [Theory]
    [InlineData(5L * 1024 * 1024, BrowserRejectReason.TooSmall)]
    [InlineData(50L * 1024 * 1024, BrowserRejectReason.None)]
    [InlineData(null, BrowserRejectReason.None)]
    [InlineData(-1L, BrowserRejectReason.None)]
    public void Evaluate_Size(long? size, BrowserRejectReason expected)
    {
        Assert.Equal(expected, BrowserHandoffPolicy.Evaluate(Enabled(), Handoff(size: size)));
    }

    [Theory]
    [InlineData("example.com", "example.com", true)]
    [InlineData("dl.example.com", "example.com", true)]
    [InlineData("badexample.com", "example.com", false)]
    [InlineData("example.com", "*.example.com", false)]
    [InlineData("a.b.example.com", "*.example.com", true)]
    public void SiteMatches_Rules(string host, string pattern, bool expected)
    {
        Assert.Equal(expected, BrowserHandoffPolicy.SiteMatches(host, pattern));
    }

    [Theory]
    [InlineData("https://Drive.Google.com/x?y=1", "drive.google.com")]
    [InlineData("  *.SharePoint.com ", "*.sharepoint.com")]
    [InlineData("user:pw@example.com:8443/path", "example.com")]
    [InlineData("b\u00fccher.de", "xn--bcher-kva.de")]
    [InlineData("localhost", null)]
    [InlineData("*.", null)]
    [InlineData("exa mple.com", null)]
    [InlineData("", null)]
    public void NormalizeSitePattern_Cases(string input, string? expected)
    {
        Assert.Equal(expected, BrowserHandoffPolicy.NormalizeSitePattern(input));
    }

    [Fact]
    public void Normalize_ClampsAndDeduplicates()
    {
        var settings = new BrowserIntegrationSettings
        {
            MinimumBytes = -5,
            ExcludedSites = ["Example.com", "example.com", "not a site", "*.a.com"]
        };

        BrowserHandoffPolicy.Normalize(settings);

        Assert.Equal(0, settings.MinimumBytes);
        Assert.Equal(["example.com", "*.a.com"], settings.ExcludedSites);
    }

    [Fact]
    public void RequestContext_ScopesCookiesByDomainPathAndSecurity()
    {
        var handoff = Handoff();
        handoff.Cookies =
        [
            new BrowserCookie { Name = "domainWide", Value = "1", Domain = ".example.com", Path = "/", HostOnly = false },
            new BrowserCookie { Name = "hostOnly", Value = "2", Domain = "files.example.com", Path = "/", HostOnly = true },
            new BrowserCookie { Name = "secureOnly", Value = "3", Domain = ".example.com", Path = "/", Secure = true },
            new BrowserCookie { Name = "otherPath", Value = "4", Domain = ".example.com", Path = "/private" },
            new BrowserCookie { Name = "expired", Value = "5", Domain = ".example.com", Path = "/", ExpirationDate = 1 },
            new BrowserCookie { Name = "otherSite", Value = "6", Domain = ".evil.test", Path = "/" }
        ];

        var context = BrowserRequestContext.Create(handoff, useCookies: true, NullLoggingService.Instance);

        var https = context.GetCookieHeader(new Uri("https://files.example.com/big.iso"));
        Assert.Contains("domainWide=1", https);
        Assert.Contains("hostOnly=2", https);
        Assert.Contains("secureOnly=3", https);
        Assert.DoesNotContain("otherPath", https);
        Assert.DoesNotContain("expired", https);
        Assert.DoesNotContain("otherSite", https);

        var sub = context.GetCookieHeader(new Uri("http://cdn.files.example.com/big.iso"));
        Assert.Contains("domainWide=1", sub);
        Assert.DoesNotContain("hostOnly", sub);
        Assert.DoesNotContain("secureOnly", sub);
    }

    [Fact]
    public void RequestContext_CookiesOff_SendsNone()
    {
        var handoff = Handoff();
        handoff.Cookies = [new BrowserCookie { Name = "a", Value = "1", Domain = ".example.com", Path = "/" }];

        var context = BrowserRequestContext.Create(handoff, useCookies: false, NullLoggingService.Instance);

        Assert.Equal(0, context.CookieCount);
        Assert.Equal(string.Empty, context.GetCookieHeader(new Uri("https://files.example.com/")));
    }

    [Fact]
    public void BrowserContext_IsNeverSaved()
    {
        var path = Path.Combine(Temp.Path, "state.json");
        var handoff = Handoff();
        handoff.Cookies = [new BrowserCookie { Name = "session", Value = "TOPSECRET", Domain = ".example.com", Path = "/" }];
        var state = new AppState();
        state.Downloads.Add(new DownloadItem
        {
            Url = handoff.Url,
            FileName = "big.iso",
            DestinationFolder = Temp.Path,
            BrowserContext = BrowserRequestContext.Create(handoff, true, NullLoggingService.Instance)
        });

        Assert.True(new JsonAppStore(path, NullLoggingService.Instance).Save(state));

        Assert.DoesNotContain("TOPSECRET", File.ReadAllText(path));
    }

    [Fact]
    public void Describe_OmitsCookieValuesAndUrlPath()
    {
        var handoff = Handoff("https://files.example.com/private/token=abc123/big.iso");
        handoff.Cookies = [new BrowserCookie { Name = "session", Value = "TOPSECRET", Domain = ".example.com", Path = "/" }];

        var text = BrowserProtocol.Describe(new BrowserRequest { Kind = BrowserRequestKind.Handoff, Handoff = handoff });

        Assert.Contains("files.example.com", text);
        Assert.DoesNotContain("TOPSECRET", text);
        Assert.DoesNotContain("abc123", text);
    }

    [Theory]
    [InlineData(BrowserRequestKind.Handoff)]
    [InlineData(BrowserRequestKind.SetEnabled)]
    [InlineData(BrowserRequestKind.SetSiteExcluded)]
    public void Validate_RejectsMissingPayload(BrowserRequestKind kind)
    {
        Assert.NotNull(BrowserProtocol.Validate(new BrowserRequest { Kind = kind }));
    }

    [Fact]
    public void Validate_RejectsTooManyCookies()
    {
        var handoff = Handoff();
        handoff.Cookies = Enumerable.Range(0, BrowserProtocol.MaxCookies + 1)
            .Select(i => new BrowserCookie { Name = $"c{i}", Value = "v", Domain = ".example.com", Path = "/" })
            .ToList();

        Assert.NotNull(BrowserProtocol.Validate(new BrowserRequest { Kind = BrowserRequestKind.Handoff, Handoff = handoff }));
    }

    [Fact]
    public async Task PipeServer_AnswersBrowserRequest()
    {
        var pipeName = $"AtraTech.DownloadSolutions.Tests.{Guid.NewGuid():N}";
        await using var server = new InstancePipeServer(
            pipeName,
            (_, _) => Task.FromResult(true),
            NullLoggingService.Instance,
            browserHandler: (request, _) => Task.FromResult(new BrowserResponse { Ok = true, Message = request.Browser, AppVersion = "9.9.9" }));
        server.Start();

        var result = await BrowserPipeClient.SendAsync(
            pipeName, new BrowserRequest { Kind = BrowserRequestKind.Hello, Browser = "Edge" }, TimeSpan.FromSeconds(5));

        Assert.Equal(BrowserPipeStatus.Ok, result.Status);
        Assert.Equal("Edge", result.Response!.Message);
        Assert.Equal("9.9.9", result.Response.AppVersion);
    }

    [Fact]
    public async Task PipeServer_WithoutBrowserHandler_ReportsNotAvailable()
    {
        var pipeName = $"AtraTech.DownloadSolutions.Tests.{Guid.NewGuid():N}";
        await using var server = new InstancePipeServer(pipeName, (_, _) => Task.FromResult(true), NullLoggingService.Instance);
        server.Start();

        var result = await BrowserPipeClient.SendAsync(pipeName, new BrowserRequest { Kind = BrowserRequestKind.Hello }, TimeSpan.FromSeconds(5));

        Assert.Equal(BrowserPipeStatus.Ok, result.Status);
        Assert.False(result.Response!.Ok);
        Assert.Equal(BrowserRejectReason.AppError, result.Response.Reason);
    }

    [Fact]
    public async Task PipeServer_HandlerException_BecomesAppError()
    {
        var pipeName = $"AtraTech.DownloadSolutions.Tests.{Guid.NewGuid():N}";
        await using var server = new InstancePipeServer(
            pipeName, (_, _) => Task.FromResult(true), NullLoggingService.Instance,
            browserHandler: (_, _) => throw new InvalidOperationException("boom"));
        server.Start();

        var result = await BrowserPipeClient.SendAsync(pipeName, new BrowserRequest { Kind = BrowserRequestKind.Hello }, TimeSpan.FromSeconds(5));

        Assert.Equal(BrowserRejectReason.AppError, result.Response!.Reason);
    }

    [Fact]
    public async Task PipeClient_ReportsUnreachable_WhenNoServer()
    {
        var result = await BrowserPipeClient.SendAsync(
            $"AtraTech.DownloadSolutions.Tests.{Guid.NewGuid():N}", new BrowserRequest { Kind = BrowserRequestKind.Hello }, TimeSpan.FromMilliseconds(300));

        Assert.Equal(BrowserPipeStatus.Unreachable, result.Status);
    }

    [Fact]
    public async Task Codec_V1MessageWithBrowserPayload_IsRejected()
    {
        using var stream = new MemoryStream();
        var bad = new InstanceMessage(InstanceMessage.CurrentVersion, [], new BrowserRequest { Kind = BrowserRequestKind.Hello });
        await InstanceMessageCodec.WriteAsync(stream, bad, CancellationToken.None);
        stream.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() => InstanceMessageCodec.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public void CookieContainer_RejectsCookieForOtherDomainOnRedirect()
    {
        // Guard for the per-item container: a cookie for example.com must not reach a redirect target elsewhere.
        var handoff = Handoff();
        handoff.Cookies = [new BrowserCookie { Name = "s", Value = "1", Domain = ".example.com", Path = "/" }];
        var context = BrowserRequestContext.Create(handoff, true, NullLoggingService.Instance);

        Assert.Equal(string.Empty, context.GetCookieHeader(new Uri("https://storage.cloudprovider.test/obj")));
    }
}
