using DownloadManagerApplet.Services.Browser;

namespace DownloadManagerApplet.NativeHost;

public interface IAppChannel
{
    Task<BrowserPipeResult> SendAsync(BrowserRequest request, TimeSpan connectTimeout, CancellationToken cancellationToken);
}

public sealed class PipeAppChannel : IAppChannel
{
    private readonly string _pipeName;

    public PipeAppChannel(string pipeName)
    {
        _pipeName = pipeName;
    }

    public Task<BrowserPipeResult> SendAsync(BrowserRequest request, TimeSpan connectTimeout, CancellationToken cancellationToken) =>
        BrowserPipeClient.SendAsync(_pipeName, request, connectTimeout, cancellationToken: cancellationToken);
}

/// <summary>
/// Forwards each browser request to the running app. When the app is closed, answers from state.json, or starts the
/// app first when the request needs it (a handoff with "start it and take over", or Open app / Open settings).
/// </summary>
public sealed class NativeHostHandler
{
    /// <summary>The app answers one request at a time, so a burst of downloads may queue at the pipe.</summary>
    public static readonly TimeSpan RunningConnectTimeout = TimeSpan.FromSeconds(12);

    /// <summary>Kept under the extension's 25 s reply timeout together with the app's handling budget.</summary>
    public static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(15);

    private readonly IAppChannel _channel;
    private readonly IAppLauncher _launcher;
    private readonly IStateReader _state;
    private readonly IHostLog _log;
    private readonly TimeSpan _startupTimeout;

    public NativeHostHandler(IAppChannel channel, IAppLauncher launcher, IStateReader state, IHostLog log, TimeSpan? startupTimeout = null)
    {
        _channel = channel;
        _launcher = launcher;
        _state = state;
        _log = log;
        _startupTimeout = startupTimeout ?? StartupTimeout;
    }

    public async Task<BrowserResponse> HandleAsync(BrowserRequest request, CancellationToken cancellationToken)
    {
        if (_launcher.IsRunning())
        {
            var result = await _channel.SendAsync(request, RunningConnectTimeout, cancellationToken).ConfigureAwait(false);
            switch (result.Status)
            {
                case BrowserPipeStatus.Ok:
                    return result.Response!;
                case BrowserPipeStatus.Failed:
                    _log.Error($"App did not handle {request.Kind}: {result.Error}");
                    return BrowserResponse.Failed(BrowserRejectReason.AppError, "The app didn't answer. Try again, or restart it.");
                default:
                    // Closing or still starting: fall through to the not-running path.
                    _log.Warn($"App instance found but its pipe was unreachable: {result.Error}");
                    break;
            }
        }

        var state = _state.Read();
        if (state.Problem is not null)
        {
            _log.Warn(state.Problem);
        }

        var config = BrowserConfig.From(state.Settings);
        var startApp = request.Kind switch
        {
            BrowserRequestKind.OpenApp or BrowserRequestKind.OpenSettings => true,
            BrowserRequestKind.Handoff => state.Settings.Enabled && state.Settings.StartAppWhenNotRunning,
            _ => false
        };

        if (!startApp)
        {
            return AnswerWhileClosed(request, state, config);
        }

        var background = request.Kind == BrowserRequestKind.Handoff;
        var launchError = _launcher.Launch(background);
        if (launchError is not null)
        {
            return NotRunning(BrowserResponse.Rejected(BrowserRejectReason.AppNotRunning, launchError, config));
        }

        var started = await WaitAndSendAsync(request, cancellationToken).ConfigureAwait(false);
        if (started is not null)
        {
            return started;
        }

        _log.Error($"App started but did not answer within {_startupTimeout.TotalSeconds:0}s");
        return NotRunning(BrowserResponse.Rejected(BrowserRejectReason.AppNotRunning, "The app is starting; the browser kept this download.", config));
    }

    private async Task<BrowserResponse?> WaitAndSendAsync(BrowserRequest request, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + _startupTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_launcher.IsRunning())
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                var result = await _channel.SendAsync(request, remaining, cancellationToken).ConfigureAwait(false);
                if (result.Status == BrowserPipeStatus.Ok)
                {
                    return result.Response;
                }

                if (result.Status == BrowserPipeStatus.Failed)
                {
                    _log.Error($"Started app did not handle {request.Kind}: {result.Error}");
                    return BrowserResponse.Failed(BrowserRejectReason.AppError, "The app didn't answer. Try again.");
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private BrowserResponse AnswerWhileClosed(BrowserRequest request, HostState state, BrowserConfig config)
    {
        switch (request.Kind)
        {
            case BrowserRequestKind.Hello:
            case BrowserRequestKind.GetConfig:
                return NotRunning(new BrowserResponse { Ok = true, Config = config });

            case BrowserRequestKind.Handoff:
                var reason = state.Settings.Enabled ? BrowserRejectReason.AppNotRunning : BrowserRejectReason.Disabled;
                _log.Debug($"Left download in the browser while the app is closed: {reason}");
                return NotRunning(BrowserResponse.Rejected(reason,
                    reason == BrowserRejectReason.Disabled ? "Browser integration is off." : "The app isn't running.", config));

            default:
                return NotRunning(BrowserResponse.Rejected(BrowserRejectReason.AppNotRunning, "Open the app to change settings.", config));
        }
    }

    private static BrowserResponse NotRunning(BrowserResponse response)
    {
        response.AppRunning = false;
        return response;
    }
}
