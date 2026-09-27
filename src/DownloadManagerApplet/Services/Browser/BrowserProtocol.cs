using System.Text.Json;
using System.Text.Json.Serialization;
using DownloadManagerApplet.Models;

namespace DownloadManagerApplet.Services.Browser;

// Shared by the app and the native host (linked source). The extension speaks the same shapes in camelCase.

public enum BrowserRequestKind
{
    Hello,
    GetConfig,
    Handoff,
    SetEnabled,
    SetSiteExcluded,
    OpenApp,
    OpenSettings
}

public enum BrowserRejectReason
{
    None,
    Disabled,
    SiteExcluded,
    TooSmall,
    PrivateWindow,
    UnsupportedUrl,
    ServerRefused,
    Timeout,
    Invalid,
    AppNotRunning,
    AppError
}

public sealed class BrowserRequest
{
    public BrowserRequestKind Kind { get; set; }

    /// <summary>"Chrome", "Edge", "Firefox", "Brave", "Opera", "Vivaldi", or "Chromium".</summary>
    public string? Browser { get; set; }

    public string? ExtensionVersion { get; set; }
    public BrowserHandoff? Handoff { get; set; }
    public bool? Enabled { get; set; }
    public string? Site { get; set; }
    public bool? Excluded { get; set; }
}

public sealed class BrowserHandoff
{
    public string Url { get; set; } = string.Empty;
    public string? FileName { get; set; }
    public string? Referrer { get; set; }
    public string? PageUrl { get; set; }

    /// <summary>Null or not positive when the browser doesn't know the size.</summary>
    public long? TotalBytes { get; set; }

    public string? MimeType { get; set; }
    public string? UserAgent { get; set; }
    public bool Incognito { get; set; }
    public List<BrowserCookie>? Cookies { get; set; }
}

public sealed class BrowserCookie
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string Path { get; set; } = "/";
    public bool Secure { get; set; }
    public bool HostOnly { get; set; }

    /// <summary>Unix seconds; null for session cookies.</summary>
    public double? ExpirationDate { get; set; }
}

public sealed class BrowserResponse
{
    public bool Ok { get; set; }
    public bool Accepted { get; set; }
    public BrowserRejectReason Reason { get; set; }
    public string? Message { get; set; }
    public bool AppRunning { get; set; } = true;
    public string? AppVersion { get; set; }
    public BrowserConfig? Config { get; set; }

    public static BrowserResponse Rejected(BrowserRejectReason reason, string? message, BrowserConfig? config = null) =>
        new() { Ok = true, Accepted = false, Reason = reason, Message = message, Config = config };

    public static BrowserResponse Failed(BrowserRejectReason reason, string message) =>
        new() { Ok = false, Accepted = false, Reason = reason, Message = message };
}

/// <summary>What the extension needs to pre-filter downloads and draw its popup.</summary>
public sealed class BrowserConfig
{
    public bool Enabled { get; set; }
    public long MinimumBytes { get; set; }
    public bool UseCookies { get; set; }
    public bool Notify { get; set; }
    public bool StartAppWhenNotRunning { get; set; }
    public List<string> ExcludedSites { get; set; } = new();

    public static BrowserConfig From(BrowserIntegrationSettings settings) => new()
    {
        Enabled = settings.Enabled,
        MinimumBytes = settings.MinimumBytes,
        UseCookies = settings.UseBrowserCookies,
        Notify = settings.ShowBrowserNotification,
        StartAppWhenNotRunning = settings.StartAppWhenNotRunning,
        ExcludedSites = settings.ExcludedSites.ToList()
    };
}

public static class BrowserProtocol
{
    public const string HostName = "com.atratech.downloadsolutions";
    public const int MaxUrlLength = 8192;
    public const int MaxCookies = 300;
    public const int MaxCookieValueLength = 8192;
    public const int MaxSiteLength = 253;
    private const int MaxShortText = 64;
    private const int MaxFileNameLength = 1024;
    private const int MaxUserAgentLength = 1024;

    /// <summary>Named pipe between the native host and the app (PascalCase, matching <see cref="InstanceMessageCodec"/>).</summary>
    public static readonly JsonSerializerOptions PipeJson = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Native Messaging between the extension and the native host.</summary>
    public static readonly JsonSerializerOptions ExtensionJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>Null when the request is well formed; otherwise why it was refused.</summary>
    public static string? Validate(BrowserRequest? request)
    {
        if (request is null)
        {
            return "Request is empty.";
        }

        if (!Enum.IsDefined(request.Kind))
        {
            return "Unknown request kind.";
        }

        if (TooLong(request.Browser, MaxShortText) || TooLong(request.ExtensionVersion, MaxShortText))
        {
            return "Browser name or extension version is too long.";
        }

        switch (request.Kind)
        {
            case BrowserRequestKind.Handoff:
                return ValidateHandoff(request.Handoff);
            case BrowserRequestKind.SetEnabled when request.Enabled is null:
                return "SetEnabled needs Enabled.";
            case BrowserRequestKind.SetSiteExcluded when string.IsNullOrWhiteSpace(request.Site) || request.Site.Length > MaxSiteLength || request.Excluded is null:
                return "SetSiteExcluded needs a site and Excluded.";
            default:
                return null;
        }
    }

    private static string? ValidateHandoff(BrowserHandoff? handoff)
    {
        if (handoff is null)
        {
            return "Handoff details are missing.";
        }

        if (string.IsNullOrWhiteSpace(handoff.Url) || handoff.Url.Length > MaxUrlLength)
        {
            return "Download URL is missing or too long.";
        }

        if (TooLong(handoff.Referrer, MaxUrlLength) || TooLong(handoff.PageUrl, MaxUrlLength) ||
            TooLong(handoff.FileName, MaxFileNameLength) || TooLong(handoff.UserAgent, MaxUserAgentLength) ||
            TooLong(handoff.MimeType, 255))
        {
            return "A handoff field is too long.";
        }

        var cookies = handoff.Cookies;
        if (cookies is null)
        {
            return null;
        }

        if (cookies.Count > MaxCookies)
        {
            return $"More than {MaxCookies} cookies.";
        }

        foreach (var cookie in cookies)
        {
            if (cookie is null || string.IsNullOrEmpty(cookie.Name) || cookie.Name.Length > MaxShortText * 4 ||
                (cookie.Value?.Length ?? 0) > MaxCookieValueLength || TooLong(cookie.Domain, MaxSiteLength + 1) ||
                TooLong(cookie.Path, 1024))
            {
                return "A cookie is malformed or too large.";
            }
        }

        return null;
    }

    /// <summary>Log-safe summary: kind, browser, and the download's host only. Never cookies, paths, or query strings.</summary>
    public static string Describe(BrowserRequest request)
    {
        var text = $"{request.Kind} from {request.Browser ?? "unknown browser"} {request.ExtensionVersion}".TrimEnd();
        if (request.Handoff is { } handoff)
        {
            var host = Uri.TryCreate(handoff.Url, UriKind.Absolute, out var uri) ? uri.Host : "invalid URL";
            text += $" ({host}, {handoff.TotalBytes?.ToString() ?? "size unknown"} bytes, {handoff.Cookies?.Count ?? 0} cookie(s))";
        }

        return text;
    }

    private static bool TooLong(string? value, int max) => value is not null && value.Length > max;
}
