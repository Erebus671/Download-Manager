using DownloadManagerApplet.Models;

namespace DownloadManagerApplet.Services.Browser;

/// <summary>An extension that has talked to the app; shown on Settings > Browser integration.</summary>
public sealed class BrowserConnection
{
    public string Browser { get; set; } = string.Empty;
    public string? ExtensionVersion { get; set; }
    public DateTimeOffset LastSeen { get; set; }
}

/// <summary>What the app does with a browser download it accepted.</summary>
public sealed record BrowserDownloadRequest(BrowserHandoff Handoff, BrowserRequestContext Context, string Browser, long? TotalBytes);

/// <summary>Answers requests from the native host. State changes run on the UI thread via <c>runOnUi</c>.</summary>
public sealed class BrowserIntegrationService
{
    private static readonly TimeSpan SeenPersistInterval = TimeSpan.FromMinutes(5);

    private static readonly HashSet<string> KnownBrowsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Chrome", "Edge", "Firefox", "Brave", "Opera", "Vivaldi", "Chromium"
    };

    private readonly AppState _state;
    private readonly Func<Action, CancellationToken, Task> _runOnUi;
    private readonly Func<bool> _persist;
    private readonly Func<BrowserDownloadRequest, bool> _addDownload;
    private readonly Action<bool> _activate;
    private readonly IHandoffProbe _probe;
    private readonly ILoggingService _log;
    private readonly string _appVersion;
    private readonly Func<DateTimeOffset> _clock;

    /// <param name="runOnUi">Runs the action on the UI thread; tests run it inline.</param>
    /// <param name="addDownload">Queues an accepted download (UI thread); false if it couldn't be queued.</param>
    /// <param name="activate">Brings the window forward; true opens Settings > Browser integration (UI thread).</param>
    public BrowserIntegrationService(
        AppState state,
        Func<Action, CancellationToken, Task> runOnUi,
        Func<bool> persist,
        Func<BrowserDownloadRequest, bool> addDownload,
        Action<bool> activate,
        IHandoffProbe probe,
        ILoggingService log,
        string appVersion,
        Func<DateTimeOffset>? clock = null)
    {
        _state = state;
        _runOnUi = runOnUi;
        _persist = persist;
        _addDownload = addDownload;
        _activate = activate;
        _probe = probe;
        _log = log;
        _appVersion = appVersion;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <summary>Raised on the UI thread after an extension checks in.</summary>
    public event Action? ConnectionsChanged;

    /// <summary>Raised on the UI thread after the extension changed a setting.</summary>
    public event Action? SettingsChanged;

    private BrowserIntegrationSettings Settings => _state.Settings.BrowserIntegration;

    public static string NormalizeBrowserName(string? browser) =>
        browser is not null && KnownBrowsers.TryGetValue(browser.Trim(), out var known) ? known : "Browser";

    public async Task<BrowserResponse> HandleAsync(BrowserRequest request, CancellationToken cancellationToken)
    {
        _log.Debug($"Browser request: {BrowserProtocol.Describe(request)}");
        var browser = NormalizeBrowserName(request.Browser);
        await _runOnUi(() => RecordSeen(browser, request.ExtensionVersion), cancellationToken);

        switch (request.Kind)
        {
            case BrowserRequestKind.Hello:
            case BrowserRequestKind.GetConfig:
                return await OkAsync(cancellationToken);

            case BrowserRequestKind.SetEnabled:
                await _runOnUi(() => ChangeSettings(s => s.Enabled = request.Enabled!.Value, $"Browser integration turned {(request.Enabled!.Value ? "on" : "off")} from {browser}"), cancellationToken);
                return await OkAsync(cancellationToken);

            case BrowserRequestKind.SetSiteExcluded:
                return await SetSiteExcludedAsync(request.Site!, request.Excluded!.Value, browser, cancellationToken);

            case BrowserRequestKind.OpenApp:
            case BrowserRequestKind.OpenSettings:
                var settingsPage = request.Kind == BrowserRequestKind.OpenSettings;
                await _runOnUi(() => _activate(settingsPage), cancellationToken);
                return await OkAsync(cancellationToken);

            case BrowserRequestKind.Handoff:
                return await HandoffAsync(request.Handoff!, browser, cancellationToken);

            default:
                return BrowserResponse.Failed(BrowserRejectReason.Invalid, "Unknown request.");
        }
    }

    private async Task<BrowserResponse> HandoffAsync(BrowserHandoff handoff, string browser, CancellationToken cancellationToken)
    {
        var host = BrowserHandoffPolicy.TryGetHttpUri(handoff.Url, out var uri) ? uri.Host : "invalid URL";
        var reason = BrowserRejectReason.None;
        var useCookies = false;
        BrowserConfig? config = null;
        await _runOnUi(() =>
        {
            reason = BrowserHandoffPolicy.Evaluate(Settings, handoff);
            useCookies = Settings.UseBrowserCookies;
            config = BrowserConfig.From(Settings);
        }, cancellationToken);

        if (reason != BrowserRejectReason.None)
        {
            _log.Debug($"Left {host} download in {browser}: {reason}");
            return Reject(reason, config);
        }

        var context = BrowserRequestContext.Create(handoff, useCookies, _log);
        var probe = await _probe.ProbeAsync(handoff, context, cancellationToken);
        if (!probe.Ok)
        {
            _log.Warn($"Left {host} download in {browser}: the app couldn't fetch it ({probe.Detail})");
            var probeReason = probe.Detail.StartsWith("no answer", StringComparison.Ordinal) ? BrowserRejectReason.Timeout : BrowserRejectReason.ServerRefused;
            return Reject(probeReason, config, probe.Detail);
        }

        var size = handoff.TotalBytes is > 0 ? handoff.TotalBytes : probe.TotalBytes;
        var added = false;
        await _runOnUi(() =>
        {
            if (BrowserHandoffPolicy.IsTooSmall(Settings, size))
            {
                reason = BrowserRejectReason.TooSmall;
                return;
            }

            added = _addDownload(new BrowserDownloadRequest(handoff, context, browser, size));
        }, cancellationToken);

        if (reason == BrowserRejectReason.TooSmall)
        {
            _log.Debug($"Left {host} download in {browser}: server reports {size} bytes, under the threshold");
            return Reject(reason, config);
        }

        if (!added)
        {
            return Reject(BrowserRejectReason.AppError, config, "The app couldn't queue the download.");
        }

        _log.Info($"Took over a download from {browser} ({host}, {(size?.ToString() ?? "size unknown")} bytes, {context.CookieCount} cookie(s))");
        return new BrowserResponse { Ok = true, Accepted = true, AppVersion = _appVersion, Config = config };
    }

    private async Task<BrowserResponse> SetSiteExcludedAsync(string site, bool excluded, string browser, CancellationToken cancellationToken)
    {
        var pattern = BrowserHandoffPolicy.NormalizeSitePattern(site);
        if (pattern is null)
        {
            return Reject(BrowserRejectReason.Invalid, null, "That isn't a site address.");
        }

        await _runOnUi(() => ChangeSettings(s =>
        {
            if (excluded)
            {
                if (!s.ExcludedSites.Contains(pattern))
                {
                    s.ExcludedSites.Add(pattern);
                }
            }
            else
            {
                // Remove every entry covering this host, so the site toggle really turns it back on.
                var host = pattern.TrimStart('*', '.');
                s.ExcludedSites.RemoveAll(p => p == pattern || BrowserHandoffPolicy.SiteMatches(host, p));
            }
        }, $"{pattern} {(excluded ? "added to" : "removed from")} excluded sites from {browser}"), cancellationToken);

        return await OkAsync(cancellationToken);
    }

    private void ChangeSettings(Action<BrowserIntegrationSettings> change, string logMessage)
    {
        change(Settings);
        _log.Info(logMessage);
        if (!_persist())
        {
            _log.Warn("Browser integration change applied but not saved");
        }

        SettingsChanged?.Invoke();
    }

    private void RecordSeen(string browser, string? extensionVersion)
    {
        var now = _clock();
        var version = extensionVersion?.Trim();
        var entry = _state.BrowserConnections.FirstOrDefault(c => string.Equals(c.Browser, browser, StringComparison.OrdinalIgnoreCase));
        var persist = entry is null || entry.ExtensionVersion != version || now - entry.LastSeen >= SeenPersistInterval;
        if (entry is null)
        {
            entry = new BrowserConnection { Browser = browser };
            _state.BrowserConnections.Add(entry);
            _log.Info($"{browser} extension {version} connected for the first time");
        }

        entry.ExtensionVersion = version;
        entry.LastSeen = now;
        if (persist)
        {
            _persist();
        }

        ConnectionsChanged?.Invoke();
    }

    private async Task<BrowserResponse> OkAsync(CancellationToken cancellationToken)
    {
        BrowserConfig? config = null;
        await _runOnUi(() => config = BrowserConfig.From(Settings), cancellationToken);
        return new BrowserResponse { Ok = true, Accepted = false, AppVersion = _appVersion, Config = config };
    }

    private BrowserResponse Reject(BrowserRejectReason reason, BrowserConfig? config, string? detail = null)
    {
        var response = BrowserResponse.Rejected(reason, detail ?? DescribeReason(reason), config);
        response.AppVersion = _appVersion;
        return response;
    }

    public static string DescribeReason(BrowserRejectReason reason) => reason switch
    {
        BrowserRejectReason.Disabled => "Browser integration is off.",
        BrowserRejectReason.SiteExcluded => "This site is excluded.",
        BrowserRejectReason.TooSmall => "Smaller than the take-over size.",
        BrowserRejectReason.PrivateWindow => "Private windows are never taken over.",
        BrowserRejectReason.UnsupportedUrl => "Not a plain web link.",
        BrowserRejectReason.ServerRefused => "The server refused the app's request.",
        BrowserRejectReason.Timeout => "The server didn't answer the app in time.",
        BrowserRejectReason.Invalid => "The request was invalid.",
        BrowserRejectReason.AppNotRunning => "The app isn't running.",
        BrowserRejectReason.AppError => "The app hit an error; see its log.",
        _ => string.Empty
    };
}
