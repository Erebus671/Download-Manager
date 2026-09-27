extern alias nativehost;

using System.IO;
using System.Text.Json;
using DownloadManagerApplet.Models;
using DownloadManagerApplet.Services;
using DotNetTestKit;
using Host = nativehost::DownloadManagerApplet.NativeHost;
using HostBrowser = nativehost::DownloadManagerApplet.Services.Browser;
using HostModels = nativehost::DownloadManagerApplet.Models;

namespace DownloadManagerApplet.Tests;

internal sealed class NullHostLog : Host.IHostLog
{
    public Host.HostLogLevel MinimumLevel { get; set; }
    public List<string> Lines { get; } = [];

    public void Write(Host.HostLogLevel level, string message) => Lines.Add($"{level}: {message}");
}

internal sealed class FakeLauncher : Host.IAppLauncher
{
    public bool Running { get; set; }
    public string? LaunchError { get; set; }
    public bool StartsApp { get; set; } = true;
    public List<bool> Launches { get; } = [];

    public bool IsRunning() => Running;

    public string? Launch(bool background)
    {
        Launches.Add(background);
        if (LaunchError is null && StartsApp)
        {
            Running = true;
        }

        return LaunchError;
    }
}

internal sealed class FakeChannel : Host.IAppChannel
{
    public HostBrowser.BrowserPipeResult Result { get; set; } =
        new(HostBrowser.BrowserPipeStatus.Ok, new HostBrowser.BrowserResponse { Ok = true, Accepted = true }, null);

    public List<HostBrowser.BrowserRequest> Sent { get; } = [];

    public Task<HostBrowser.BrowserPipeResult> SendAsync(HostBrowser.BrowserRequest request, TimeSpan connectTimeout, CancellationToken cancellationToken)
    {
        Sent.Add(request);
        return Task.FromResult(Result);
    }
}

internal sealed class FixedState : Host.IStateReader
{
    public HostModels.BrowserIntegrationSettings Settings { get; } = new();

    public Host.HostState Read() => new(Settings, Host.HostLogLevel.Info, null);
}

public class NativeHostTests : TestBase
{
    private readonly FakeLauncher _launcher = new();
    private readonly FakeChannel _channel = new();
    private readonly FixedState _state = new();
    private readonly NullHostLog _log = new();

    private Host.NativeHostHandler NewHandler() => new(_channel, _launcher, _state, _log, TimeSpan.FromSeconds(1));

    private static HostBrowser.BrowserRequest Request(HostBrowser.BrowserRequestKind kind) => new()
    {
        Kind = kind,
        Browser = "Chrome",
        Handoff = kind == HostBrowser.BrowserRequestKind.Handoff ? new HostBrowser.BrowserHandoff { Url = "https://example.com/a.iso" } : null
    };

    [Fact]
    public async Task Running_ForwardsToApp()
    {
        _launcher.Running = true;

        var response = await NewHandler().HandleAsync(Request(HostBrowser.BrowserRequestKind.Handoff), CancellationToken.None);

        Assert.True(response.Accepted);
        Assert.Single(_channel.Sent);
        Assert.Empty(_launcher.Launches);
    }

    [Fact]
    public async Task Running_PipeFailure_IsAppError()
    {
        _launcher.Running = true;
        _channel.Result = new(HostBrowser.BrowserPipeStatus.Failed, null, "broken");

        var response = await NewHandler().HandleAsync(Request(HostBrowser.BrowserRequestKind.Hello), CancellationToken.None);

        Assert.False(response.Ok);
        Assert.Equal(HostBrowser.BrowserRejectReason.AppError, response.Reason);
    }

    [Fact]
    public async Task Closed_Hello_AnswersFromState()
    {
        _state.Settings.Enabled = true;
        _state.Settings.ExcludedSites.Add("example.com");

        var response = await NewHandler().HandleAsync(Request(HostBrowser.BrowserRequestKind.Hello), CancellationToken.None);

        Assert.True(response.Ok);
        Assert.False(response.AppRunning);
        Assert.True(response.Config!.Enabled);
        Assert.Equal(["example.com"], response.Config.ExcludedSites);
        Assert.Empty(_launcher.Launches);
        Assert.Empty(_channel.Sent);
    }

    [Fact]
    public async Task Closed_Handoff_StartsAppInBackgroundAndForwards()
    {
        _state.Settings.Enabled = true;
        _state.Settings.StartAppWhenNotRunning = true;

        var response = await NewHandler().HandleAsync(Request(HostBrowser.BrowserRequestKind.Handoff), CancellationToken.None);

        Assert.True(response.Accepted);
        Assert.Equal([true], _launcher.Launches);
        Assert.Single(_channel.Sent);
    }

    [Fact]
    public async Task Closed_Handoff_StartDisabled_StaysInBrowser()
    {
        _state.Settings.Enabled = true;
        _state.Settings.StartAppWhenNotRunning = false;

        var response = await NewHandler().HandleAsync(Request(HostBrowser.BrowserRequestKind.Handoff), CancellationToken.None);

        Assert.False(response.Accepted);
        Assert.Equal(HostBrowser.BrowserRejectReason.AppNotRunning, response.Reason);
        Assert.Empty(_launcher.Launches);
    }

    [Fact]
    public async Task Closed_Handoff_FeatureOff_Disabled()
    {
        var response = await NewHandler().HandleAsync(Request(HostBrowser.BrowserRequestKind.Handoff), CancellationToken.None);

        Assert.Equal(HostBrowser.BrowserRejectReason.Disabled, response.Reason);
        Assert.Empty(_launcher.Launches);
    }

    [Fact]
    public async Task Closed_OpenApp_LaunchesInForeground_EvenWhenOff()
    {
        await NewHandler().HandleAsync(Request(HostBrowser.BrowserRequestKind.OpenApp), CancellationToken.None);

        Assert.Equal([false], _launcher.Launches);
    }

    [Fact]
    public async Task Closed_LaunchFails_AppNotRunning()
    {
        _state.Settings.Enabled = true;
        _launcher.LaunchError = "missing";

        var response = await NewHandler().HandleAsync(Request(HostBrowser.BrowserRequestKind.Handoff), CancellationToken.None);

        Assert.Equal(HostBrowser.BrowserRejectReason.AppNotRunning, response.Reason);
        Assert.False(response.AppRunning);
        Assert.Empty(_channel.Sent);
    }

    [Fact]
    public async Task Closed_AppNeverAnswers_TimesOut()
    {
        _state.Settings.Enabled = true;
        _launcher.StartsApp = false;

        var response = await NewHandler().HandleAsync(Request(HostBrowser.BrowserRequestKind.Handoff), CancellationToken.None);

        Assert.Equal(HostBrowser.BrowserRejectReason.AppNotRunning, response.Reason);
        Assert.Contains(_log.Lines, l => l.Contains("did not answer"));
    }

    [Fact]
    public async Task Closed_SettingChange_AsksToOpenApp()
    {
        var request = Request(HostBrowser.BrowserRequestKind.SetEnabled);
        request.Enabled = true;

        var response = await NewHandler().HandleAsync(request, CancellationToken.None);

        Assert.Equal(HostBrowser.BrowserRejectReason.AppNotRunning, response.Reason);
        Assert.Empty(_launcher.Launches);
    }

    [Fact]
    public async Task Codec_ReadsCamelCaseRequest()
    {
        var json = """{"kind":"handoff","browser":"Firefox","handoff":{"url":"https://example.com/a.iso","totalBytes":123,"cookies":[{"name":"a","value":"b","domain":".example.com","path":"/","hostOnly":false}]}}""";

        var result = await Host.NativeMessagingCodec.ReadAsync(new MemoryStream(Host.NativeMessagingCodec.Frame(json)), CancellationToken.None);

        Assert.Equal(Host.NativeReadStatus.Message, result.Status);
        Assert.Equal(HostBrowser.BrowserRequestKind.Handoff, result.Request!.Kind);
        Assert.Equal(123, result.Request.Handoff!.TotalBytes);
        Assert.Single(result.Request.Handoff.Cookies!);
    }

    [Fact]
    public async Task Codec_WritesCamelCaseResponse()
    {
        using var stream = new MemoryStream();
        await Host.NativeMessagingCodec.WriteAsync(stream, HostBrowser.BrowserResponse.Rejected(HostBrowser.BrowserRejectReason.SiteExcluded, "x"), CancellationToken.None);

        var json = System.Text.Encoding.UTF8.GetString(stream.ToArray(), 4, (int)stream.Length - 4);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("siteExcluded", doc.RootElement.GetProperty("reason").GetString());
        Assert.True(doc.RootElement.GetProperty("appRunning").GetBoolean());
        Assert.Equal(stream.Length - 4, BitConverter.ToUInt32(stream.ToArray(), 0));
    }

    [Theory]
    [InlineData("not json", Host.NativeReadStatus.Malformed)]
    [InlineData("""{"kind":"handoff"}""", Host.NativeReadStatus.Malformed)]
    [InlineData("""{"kind":"nonsense"}""", Host.NativeReadStatus.Malformed)]
    public async Task Codec_RejectsBadMessages(string json, Host.NativeReadStatus expected)
    {
        var result = await Host.NativeMessagingCodec.ReadAsync(new MemoryStream(Host.NativeMessagingCodec.Frame(json)), CancellationToken.None);

        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public async Task Codec_TooLarge_IsDrainedSoTheNextMessageReads()
    {
        var big = new byte[4 + Host.NativeMessagingCodec.MaxMessageBytes + 1];
        BitConverter.GetBytes((uint)(Host.NativeMessagingCodec.MaxMessageBytes + 1)).CopyTo(big, 0);
        var next = Host.NativeMessagingCodec.Frame("""{"kind":"hello"}""");
        var stream = new MemoryStream([.. big, .. next]);

        var first = await Host.NativeMessagingCodec.ReadAsync(stream, CancellationToken.None);
        var second = await Host.NativeMessagingCodec.ReadAsync(stream, CancellationToken.None);

        Assert.Equal(Host.NativeReadStatus.TooLarge, first.Status);
        Assert.Equal(Host.NativeReadStatus.Message, second.Status);
    }

    [Fact]
    public async Task Codec_EmptyStream_IsEndOfStream()
    {
        var result = await Host.NativeMessagingCodec.ReadAsync(new MemoryStream(), CancellationToken.None);

        Assert.Equal(Host.NativeReadStatus.EndOfStream, result.Status);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task HostLoop_AnswersEachMessageUntilEof()
    {
        _launcher.Running = true;
        var input = new MemoryStream([.. Host.NativeMessagingCodec.Frame("""{"kind":"hello"}"""), .. Host.NativeMessagingCodec.Frame("garbage"), .. Host.NativeMessagingCodec.Frame("""{"kind":"getConfig"}""")]);
        var output = new MemoryStream();

        var code = await Host.HostLoop.RunAsync(input, output, NewHandler(), _log, CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Equal(2, _channel.Sent.Count);
        Assert.Equal(3, CountFrames(output.ToArray()));
    }

    [Fact]
    public void StateReader_ReadsWhatTheAppSaves()
    {
        var path = Path.Combine(Temp.Path, "state.json");
        var state = new AppState();
        state.Settings.MinimumLogLevel = LogLevelSetting.Debug;
        state.Settings.BrowserIntegration.Enabled = true;
        state.Settings.BrowserIntegration.StartAppWhenNotRunning = false;
        state.Settings.BrowserIntegration.MinimumBytes = 42;
        state.Settings.BrowserIntegration.ExcludedSites.Add("example.com");
        Assert.True(new JsonAppStore(path, NullLoggingService.Instance).Save(state));

        var read = new Host.StateFileReader(path).Read();

        Assert.Null(read.Problem);
        Assert.Equal(Host.HostLogLevel.Debug, read.LogLevel);
        Assert.True(read.Settings.Enabled);
        Assert.False(read.Settings.StartAppWhenNotRunning);
        Assert.Equal(42, read.Settings.MinimumBytes);
        Assert.Equal(["example.com"], read.Settings.ExcludedSites);
    }

    [Fact]
    public void StateReader_MissingFile_Defaults()
    {
        var read = new Host.StateFileReader(Path.Combine(Temp.Path, "nope.json")).Read();

        Assert.Null(read.Problem);
        Assert.False(read.Settings.Enabled);
    }

    [Fact]
    public void StateReader_CorruptFile_DefaultsWithProblem()
    {
        var path = Path.Combine(Temp.Path, "state.json");
        File.WriteAllText(path, "{ not json");

        var read = new Host.StateFileReader(path).Read();

        Assert.NotNull(read.Problem);
        Assert.False(read.Settings.Enabled);
    }

    [Theory]
    [InlineData(new[] { "chrome-extension://appmhpafdcnnfglmahilhiffokijfpjb/", "--parent-window=0" }, true)]
    [InlineData(new[] { "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/" }, false)]
    [InlineData(new[] { @"C:\x\manifest.json", "downloadsolutions@atratech" }, true)]
    [InlineData(new[] { @"C:\x\manifest.json", "evil@addon" }, false)]
    [InlineData(new string[0], false)]
    public void CallerCheck_AllowsOnlyKnownExtensions(string[] args, bool allowed)
    {
        Assert.Equal(allowed, Host.CallerCheck.IsAllowed(args));
    }

    [Fact]
    public void HostLog_GatesByLevel()
    {
        var log = new Host.HostLog(Temp.Path, Host.HostLogLevel.Warn);
        Host.HostLogExtensions.Debug(log, "hidden");
        Host.HostLogExtensions.Warn(log, "shown");

        var text = File.ReadAllText(Directory.GetFiles(Temp.Path, "nativehost-*.txt").Single());
        Assert.Contains("shown", text);
        Assert.DoesNotContain("hidden", text);
    }

    private static int CountFrames(byte[] data)
    {
        var count = 0;
        for (var offset = 0; offset + 4 <= data.Length; count++)
        {
            offset += 4 + (int)BitConverter.ToUInt32(data, offset);
        }

        return count;
    }
}
