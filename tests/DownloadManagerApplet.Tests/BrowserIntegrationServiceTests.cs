using System.IO;
using DownloadManagerApplet.Models;
using DownloadManagerApplet.Services;
using DownloadManagerApplet.Services.Browser;
using DownloadManagerApplet.ViewModels;
using DotNetTestKit;
using Microsoft.Win32;

namespace DownloadManagerApplet.Tests;

internal sealed class FakeProbe : IHandoffProbe
{
    public ProbeResult Result { get; set; } = new(true, null, "server answered 206");
    public int Calls { get; private set; }

    public Task<ProbeResult> ProbeAsync(BrowserHandoff handoff, BrowserRequestContext context, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(Result);
    }
}

public class BrowserIntegrationServiceTests : TestBase
{
    private readonly AppState _state = new();
    private readonly FakeProbe _probe = new();
    private readonly List<BrowserDownloadRequest> _added = [];
    private readonly List<bool> _activations = [];
    private DateTimeOffset _now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
    private int _saves;

    private BrowserIntegrationService NewService() => new(
        _state,
        (action, _) =>
        {
            action();
            return Task.CompletedTask;
        },
        () =>
        {
            _saves++;
            return true;
        },
        request =>
        {
            _added.Add(request);
            return true;
        },
        _activations.Add,
        _probe,
        NullLoggingService.Instance,
        "1.3.0",
        () => _now);

    private static BrowserRequest HandoffRequest(long? size = null, string url = "https://example.com/big.iso") => new()
    {
        Kind = BrowserRequestKind.Handoff,
        Browser = "Chrome",
        ExtensionVersion = "1.3.0",
        Handoff = new BrowserHandoff { Url = url, TotalBytes = size, FileName = "big.iso" }
    };

    [Fact]
    public async Task Handoff_Accepted_QueuesWithProbeSize()
    {
        _state.Settings.BrowserIntegration.Enabled = true;
        _probe.Result = new ProbeResult(true, 700L * 1024 * 1024, "server answered 206");

        var response = await NewService().HandleAsync(HandoffRequest(), CancellationToken.None);

        Assert.True(response.Accepted);
        var added = Assert.Single(_added);
        Assert.Equal(700L * 1024 * 1024, added.TotalBytes);
        Assert.Equal("Chrome", added.Browser);
        Assert.Empty(_activations);
    }

    [Theory]
    [InlineData("server.iso", "redirect.iso", "big.iso", "server.iso")]
    [InlineData(null, "redirect.iso", "big.iso", "big.iso")]
    [InlineData(null, "redirect.iso", null, "redirect.iso")]
    [InlineData(null, null, null, null)]
    public async Task Handoff_FileName_PrefersServerThenBrowserThenRedirect(string? header, string? redirect, string? browserName, string? expected)
    {
        _state.Settings.BrowserIntegration.Enabled = true;
        _probe.Result = new ProbeResult(true, null, "server answered 206", header, redirect);
        var request = HandoffRequest();
        request.Handoff!.FileName = browserName;

        await NewService().HandleAsync(request, CancellationToken.None);

        Assert.Equal(expected, Assert.Single(_added).FileName);
    }

    [Theory]
    [InlineData("attachment; filename=\"report 2026.pdf\"", "/start", "report 2026.pdf", "real.iso")]
    [InlineData(null, "/start", null, "real.iso")]
    [InlineData(null, "/files/real.iso", null, "real.iso")]
    public async Task HttpProbe_ReadsDispositionAndRedirectNames(string? disposition, string path, string? expectedHeader, string? expectedRedirect)
    {
        await using var server = new TinyHttpServer(disposition);
        var handoff = new BrowserHandoff { Url = server.BaseUrl + path.TrimStart('/') };
        var context = BrowserRequestContext.Create(handoff, false, NullLoggingService.Instance);

        var result = await new HttpHandoffProbe(TimeSpan.FromSeconds(5)).ProbeAsync(handoff, context, CancellationToken.None);

        Assert.True(result.Ok, result.Detail);
        Assert.Equal(expectedHeader, result.HeaderFileName);
        Assert.Equal(expectedRedirect, result.RedirectFileName);
        Assert.Equal(4096, result.TotalBytes);
    }

    [Fact]
    public async Task Handoff_Disabled_DoesNotProbe()
    {
        var response = await NewService().HandleAsync(HandoffRequest(), CancellationToken.None);

        Assert.False(response.Accepted);
        Assert.Equal(BrowserRejectReason.Disabled, response.Reason);
        Assert.Equal(0, _probe.Calls);
        Assert.NotNull(response.Config);
    }

    [Theory]
    [InlineData("server answered 403", BrowserRejectReason.ServerRefused)]
    [InlineData("no answer within 3s", BrowserRejectReason.Timeout)]
    public async Task Handoff_ProbeFailure_StaysInBrowser(string detail, BrowserRejectReason expected)
    {
        _state.Settings.BrowserIntegration.Enabled = true;
        _probe.Result = new ProbeResult(false, null, detail);

        var response = await NewService().HandleAsync(HandoffRequest(), CancellationToken.None);

        Assert.Equal(expected, response.Reason);
        Assert.Empty(_added);
    }

    [Fact]
    public async Task Handoff_ProbeRevealsSmallFile_TooSmall()
    {
        _state.Settings.BrowserIntegration.Enabled = true;
        _probe.Result = new ProbeResult(true, 1024, "server answered 206");

        var response = await NewService().HandleAsync(HandoffRequest(), CancellationToken.None);

        Assert.Equal(BrowserRejectReason.TooSmall, response.Reason);
        Assert.Empty(_added);
    }

    [Fact]
    public async Task Handoff_UnknownSizeEverywhere_IsTakenOver()
    {
        _state.Settings.BrowserIntegration.Enabled = true;

        var response = await NewService().HandleAsync(HandoffRequest(), CancellationToken.None);

        Assert.True(response.Accepted);
        Assert.Null(Assert.Single(_added).TotalBytes);
    }

    [Fact]
    public async Task SetSiteExcluded_AddsThenRemovesCoveringEntries()
    {
        var service = NewService();
        await service.HandleAsync(new BrowserRequest { Kind = BrowserRequestKind.SetSiteExcluded, Site = "https://Drive.Google.com/x", Excluded = true }, CancellationToken.None);
        _state.Settings.BrowserIntegration.ExcludedSites.Add("google.com");

        Assert.Contains("drive.google.com", _state.Settings.BrowserIntegration.ExcludedSites);

        await service.HandleAsync(new BrowserRequest { Kind = BrowserRequestKind.SetSiteExcluded, Site = "drive.google.com", Excluded = false }, CancellationToken.None);

        Assert.Empty(_state.Settings.BrowserIntegration.ExcludedSites);
    }

    [Fact]
    public async Task SetSiteExcluded_InvalidSite_Rejected()
    {
        var response = await NewService().HandleAsync(
            new BrowserRequest { Kind = BrowserRequestKind.SetSiteExcluded, Site = "not a site", Excluded = true }, CancellationToken.None);

        Assert.Equal(BrowserRejectReason.Invalid, response.Reason);
    }

    [Fact]
    public async Task SetEnabled_PersistsAndRaisesEvent()
    {
        var service = NewService();
        var raised = 0;
        service.SettingsChanged += () => raised++;

        await service.HandleAsync(new BrowserRequest { Kind = BrowserRequestKind.SetEnabled, Enabled = true }, CancellationToken.None);

        Assert.True(_state.Settings.BrowserIntegration.Enabled);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task Hello_RecordsConnection_AndThrottlesSaves()
    {
        var service = NewService();
        var hello = new BrowserRequest { Kind = BrowserRequestKind.Hello, Browser = "edge", ExtensionVersion = "1.3.0" };

        await service.HandleAsync(hello, CancellationToken.None);
        await service.HandleAsync(hello, CancellationToken.None);
        var savesAfterTwo = _saves;
        _now = _now.AddMinutes(6);
        await service.HandleAsync(hello, CancellationToken.None);

        var connection = Assert.Single(_state.BrowserConnections);
        Assert.Equal("Edge", connection.Browser);
        Assert.Equal(1, savesAfterTwo);
        Assert.Equal(2, _saves);
        Assert.Equal(_now, connection.LastSeen);
    }

    [Fact]
    public async Task UnknownBrowserName_IsNormalized()
    {
        await NewService().HandleAsync(new BrowserRequest { Kind = BrowserRequestKind.Hello, Browser = "<script>" }, CancellationToken.None);

        Assert.Equal("Browser", Assert.Single(_state.BrowserConnections).Browser);
    }

    [Theory]
    [InlineData(BrowserRequestKind.OpenApp, false)]
    [InlineData(BrowserRequestKind.OpenSettings, true)]
    public async Task Open_Activates(BrowserRequestKind kind, bool settingsPage)
    {
        await NewService().HandleAsync(new BrowserRequest { Kind = kind }, CancellationToken.None);

        Assert.Equal([settingsPage], _activations);
    }

    [Fact]
    public void ViewModel_ConnectionStates()
    {
        _state.BrowserConnections.Add(new BrowserConnection { Browser = "Chrome", ExtensionVersion = "1.3.0", LastSeen = _now.AddMinutes(-5) });
        _state.BrowserConnections.Add(new BrowserConnection { Browser = "Firefox", LastSeen = _now.AddMinutes(-20) });

        var vm = new BrowserIntegrationViewModel(_state, null, NullLoggingService.Instance, () => _now, _ => { });

        Assert.Equal(BrowserRowState.Connected, vm.Browsers[0].State);
        Assert.Equal(BrowserRowState.NotDetected, vm.Browsers[1].State);
        Assert.Equal(BrowserRowState.NotConnected, vm.Browsers[2].State);
        Assert.True(vm.Browsers[1].ShowGetExtension);
        Assert.False(vm.Browsers[0].ShowGetExtension);
    }

    [Fact]
    public void ViewModel_RefreshAgesRowWhenCheckInsStop()
    {
        var clock = _now;
        _state.BrowserConnections.Add(new BrowserConnection { Browser = "Edge", ExtensionVersion = "1.3.0", LastSeen = _now });
        var vm = new BrowserIntegrationViewModel(_state, null, NullLoggingService.Instance, () => clock, _ => { });
        Assert.Equal(BrowserRowState.Connected, vm.Browsers[1].State);

        clock = _now + BrowserIntegrationViewModel.ConnectedWindow + TimeSpan.FromSeconds(1);
        vm.RefreshConnections();

        Assert.Equal(BrowserRowState.NotConnected, vm.Browsers[1].State);
        Assert.True(vm.Browsers[1].ShowGetExtension);
    }

    [Theory]
    [InlineData("25", null, 25L * 1024 * 1024)]
    [InlineData("0.5", null, 512L * 1024)]
    [InlineData("-1", "error", 10L * 1024 * 1024)]
    [InlineData("abc", "error", 10L * 1024 * 1024)]
    public void ViewModel_ApplyPendingEdits(string text, string? error, long expectedBytes)
    {
        var vm = new BrowserIntegrationViewModel(_state, null, NullLoggingService.Instance, () => _now, _ => { });
        vm.MinimumMegabytesText = text;

        var result = vm.ApplyPendingEdits();

        Assert.Equal(error is null, result is null);
        Assert.Equal(expectedBytes, _state.Settings.BrowserIntegration.MinimumBytes);
    }

    [Fact]
    public void ViewModel_AddAndRemoveSite()
    {
        var vm = new BrowserIntegrationViewModel(_state, null, NullLoggingService.Instance, () => _now, _ => { });

        vm.NewSite = "bad site";
        vm.AddSiteCommand.Execute(null);
        Assert.NotNull(vm.SiteError);

        vm.NewSite = "https://Example.com/path";
        vm.AddSiteCommand.Execute(null);
        Assert.Null(vm.SiteError);
        Assert.Equal(["example.com"], _state.Settings.BrowserIntegration.ExcludedSites);

        vm.ExcludedSites[0].RemoveCommand.Execute(null);
        Assert.Empty(_state.Settings.BrowserIntegration.ExcludedSites);
        Assert.Empty(vm.ExcludedSites);
    }

    [Fact]
    public void Registration_WritesManifestsAndKeys()
    {
        var hostExe = Path.Combine(Temp.Path, NativeHostRegistration.HostExeName);
        File.WriteAllText(hostExe, "stub");
        var keyPath = $@"Software\AtraTech.Tests\{Guid.NewGuid():N}";
        try
        {
            using var root = Registry.CurrentUser.CreateSubKey(keyPath);
            var registration = new NativeHostRegistration(root, Path.Combine(Temp.Path, "NativeMessaging"), hostExe, NullLoggingService.Instance);

            Assert.False(registration.GetStatus().Registered);
            Assert.Null(registration.Register());
            Assert.True(registration.GetStatus().Registered);

            using var chrome = root.OpenSubKey($@"Software\Google\Chrome\NativeMessagingHosts\{BrowserProtocol.HostName}");
            Assert.Equal(registration.ChromiumManifestPath, chrome!.GetValue(string.Empty));
            using var firefox = root.OpenSubKey($@"Software\Mozilla\NativeMessagingHosts\{BrowserProtocol.HostName}");
            Assert.Equal(registration.FirefoxManifestPath, firefox!.GetValue(string.Empty));

            var chromium = File.ReadAllText(registration.ChromiumManifestPath);
            Assert.Contains($"chrome-extension://{BrowserExtensionIds.ChromiumDev}/", chromium);
            Assert.Contains(BrowserExtensionIds.Firefox, File.ReadAllText(registration.FirefoxManifestPath));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void Registration_MissingHostExe_ReportsProblem()
    {
        var keyPath = $@"Software\AtraTech.Tests\{Guid.NewGuid():N}";
        try
        {
            using var root = Registry.CurrentUser.CreateSubKey(keyPath);
            var registration = new NativeHostRegistration(root, Path.Combine(Temp.Path, "nm"), Path.Combine(Temp.Path, "missing.exe"), NullLoggingService.Instance);

            Assert.NotNull(registration.Register());
            Assert.False(registration.GetStatus().Registered);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void Registration_ExePathMoved_IsNotRegistered()
    {
        var hostExe = Path.Combine(Temp.Path, NativeHostRegistration.HostExeName);
        File.WriteAllText(hostExe, "stub");
        var movedDir = Directory.CreateDirectory(Path.Combine(Temp.Path, "moved")).FullName;
        var movedExe = Path.Combine(movedDir, NativeHostRegistration.HostExeName);
        File.WriteAllText(movedExe, "stub");
        var keyPath = $@"Software\AtraTech.Tests\{Guid.NewGuid():N}";
        try
        {
            using var root = Registry.CurrentUser.CreateSubKey(keyPath);
            var folder = Path.Combine(Temp.Path, "nm");
            new NativeHostRegistration(root, folder, hostExe, NullLoggingService.Instance).Register();

            Assert.False(new NativeHostRegistration(root, folder, movedExe, NullLoggingService.Instance).GetStatus().Registered);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void Unregister_RemovesOnlyOurKeysAndManifests()
    {
        var hostExe = Path.Combine(Temp.Path, NativeHostRegistration.HostExeName);
        File.WriteAllText(hostExe, "stub");
        var keyPath = $@"Software\AtraTech.Tests\{Guid.NewGuid():N}";
        try
        {
            using var root = Registry.CurrentUser.CreateSubKey(keyPath);
            var folder = Path.Combine(Temp.Path, "NativeMessaging");
            var registration = new NativeHostRegistration(root, folder, hostExe, NullLoggingService.Instance);
            Assert.Null(registration.Register());
            using (var other = root.CreateSubKey(@"Software\Google\Chrome\NativeMessagingHosts\com.other.host"))
            {
                other.SetValue(string.Empty, "other.json");
            }

            Assert.True(registration.Unregister());

            Assert.Null(root.OpenSubKey($@"Software\Google\Chrome\NativeMessagingHosts\{BrowserProtocol.HostName}"));
            Assert.Null(root.OpenSubKey($@"Software\Mozilla\NativeMessagingHosts\{BrowserProtocol.HostName}"));
            Assert.NotNull(root.OpenSubKey(@"Software\Google\Chrome\NativeMessagingHosts\com.other.host"));
            Assert.False(Directory.Exists(folder));
            Assert.False(registration.GetStatus().Registered);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void Unregister_NothingRegistered_Succeeds()
    {
        var keyPath = $@"Software\AtraTech.Tests\{Guid.NewGuid():N}";
        try
        {
            using var root = Registry.CurrentUser.CreateSubKey(keyPath);
            var registration = new NativeHostRegistration(root, Path.Combine(Temp.Path, "nm"), Path.Combine(Temp.Path, "missing.exe"), NullLoggingService.Instance);

            Assert.True(registration.Unregister());
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void JsonAppStore_LoadsLegacyStateWithoutBrowserSection()
    {
        var path = Path.Combine(Temp.Path, "state.json");
        File.WriteAllText(path, """{"Settings":{"MaxConcurrentDownloads":3},"Downloads":[]}""");

        var state = new JsonAppStore(path, NullLoggingService.Instance).Load();

        Assert.False(state.Settings.BrowserIntegration.Enabled);
        Assert.Equal(BrowserIntegrationSettings.DefaultMinimumBytes, state.Settings.BrowserIntegration.MinimumBytes);
        Assert.Empty(state.BrowserConnections);
    }
}

/// <summary>Loopback HTTP server: /start redirects to /files/real.iso, which answers 206 with an optional Content-Disposition.</summary>
internal sealed class TinyHttpServer : IAsyncDisposable
{
    private readonly System.Net.Sockets.TcpListener _listener = new(System.Net.IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly string? _disposition;
    private readonly Task _loop;

    public TinyHttpServer(string? disposition)
    {
        _disposition = disposition;
        _listener.Start();
        _loop = Task.Run(ServeAsync);
    }

    public string BaseUrl => $"http://127.0.0.1:{((System.Net.IPEndPoint)_listener.LocalEndpoint).Port}/";

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            System.Net.Sockets.TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or System.Net.Sockets.SocketException or ObjectDisposedException)
            {
                return;
            }

            using (client)
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync() ?? string.Empty;
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync()))
                {
                }

                var redirect = requestLine.Contains(" /start ", StringComparison.Ordinal);
                var head = redirect
                    ? "HTTP/1.1 302 Found\r\nLocation: /files/real.iso\r\nContent-Length: 0\r\n"
                    : "HTTP/1.1 206 Partial Content\r\nContent-Range: bytes 0-0/4096\r\nContent-Length: 1\r\n"
                      + (_disposition is null ? string.Empty : $"Content-Disposition: {_disposition}\r\n");
                var bytes = System.Text.Encoding.ASCII.GetBytes(head + "Connection: close\r\n\r\n" + (redirect ? string.Empty : "x"));
                await stream.WriteAsync(bytes);
                await stream.FlushAsync();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await _loop.WaitAsync(TimeSpan.FromSeconds(5));
        _stop.Dispose();
    }
}
