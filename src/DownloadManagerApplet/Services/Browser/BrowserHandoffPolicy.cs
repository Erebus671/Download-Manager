using System.Globalization;
using DownloadManagerApplet.Models;

namespace DownloadManagerApplet.Services.Browser;

/// <summary>Decides which browser downloads the app takes over. The extension pre-filters with the same rules; the app has the final say.</summary>
public static class BrowserHandoffPolicy
{
    public const long MaxMinimumBytes = 1024L * 1024 * 1024 * 1024;
    private const int MaxExcludedSites = 500;
    private static readonly IdnMapping Idn = new();

    public static BrowserRejectReason Evaluate(BrowserIntegrationSettings settings, BrowserHandoff handoff)
    {
        if (!settings.Enabled)
        {
            return BrowserRejectReason.Disabled;
        }

        if (handoff.Incognito)
        {
            return BrowserRejectReason.PrivateWindow;
        }

        if (!TryGetHttpUri(handoff.Url, out var uri))
        {
            return BrowserRejectReason.UnsupportedUrl;
        }

        if (IsExcluded(settings, uri.Host) ||
            (TryGetHttpUri(handoff.PageUrl, out var page) && IsExcluded(settings, page.Host)) ||
            (TryGetHttpUri(handoff.Referrer, out var referrer) && IsExcluded(settings, referrer.Host)))
        {
            return BrowserRejectReason.SiteExcluded;
        }

        return IsTooSmall(settings, handoff.TotalBytes) ? BrowserRejectReason.TooSmall : BrowserRejectReason.None;
    }

    /// <summary>Unknown sizes (null or not positive) are never too small.</summary>
    public static bool IsTooSmall(BrowserIntegrationSettings settings, long? totalBytes) =>
        totalBytes is > 0 and var size && size < settings.MinimumBytes;

    public static bool IsExcluded(BrowserIntegrationSettings settings, string host)
    {
        var normalizedHost = NormalizeHost(host);
        return normalizedHost is not null && settings.ExcludedSites.Any(pattern => SiteMatches(normalizedHost, pattern));
    }

    /// <summary>"example.com" matches the site and its subdomains; "*.example.com" matches subdomains only.</summary>
    public static bool SiteMatches(string host, string pattern)
    {
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(pattern))
        {
            return false;
        }

        if (pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            var domain = pattern[2..];
            return host.Length > domain.Length && host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(host, pattern, StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith("." + pattern, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Turns user input ("https://Drive.Google.com/x", "*.sharepoint.com") into a stored pattern, or null if it isn't a site.
    /// </summary>
    public static string? NormalizeSitePattern(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var text = input.Trim();
        var wildcard = text.StartsWith("*.", StringComparison.Ordinal);
        if (wildcard)
        {
            text = text[2..];
        }

        var schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
        {
            text = text[(schemeEnd + 3)..];
        }

        var cut = text.IndexOfAny(['/', '?', '#']);
        if (cut >= 0)
        {
            text = text[..cut];
        }

        var at = text.LastIndexOf('@');
        if (at >= 0)
        {
            text = text[(at + 1)..];
        }

        var colon = text.IndexOf(':');
        if (colon >= 0)
        {
            text = text[..colon];
        }

        var host = NormalizeHost(text);
        if (host is null || !host.Contains('.'))
        {
            return null;
        }

        return wildcard ? "*." + host : host;
    }

    /// <summary>Lower-case ASCII (punycode) host with valid labels, or null.</summary>
    public static string? NormalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        string ascii;
        try
        {
            ascii = Idn.GetAscii(host.Trim().TrimEnd('.')).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (ascii.Length is 0 or > BrowserProtocol.MaxSiteLength)
        {
            return null;
        }

        foreach (var label in ascii.Split('.'))
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-' ||
                !label.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
            {
                return null;
            }
        }

        return ascii;
    }

    /// <summary>Normalizes, de-duplicates, and caps a loaded list; drops entries that aren't sites.</summary>
    public static List<string> NormalizeSiteList(IEnumerable<string?>? sites) =>
        (sites ?? [])
        .Select(NormalizeSitePattern)
        .OfType<string>()
        .Distinct(StringComparer.Ordinal)
        .Take(MaxExcludedSites)
        .ToList();

    public static void Normalize(BrowserIntegrationSettings settings)
    {
        settings.ExcludedSites = NormalizeSiteList(settings.ExcludedSites);
        settings.MinimumBytes = Math.Clamp(settings.MinimumBytes, 0, MaxMinimumBytes);
    }

    public static bool TryGetHttpUri(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed) &&
            (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps) &&
            !string.IsNullOrEmpty(parsed.Host))
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }
}
