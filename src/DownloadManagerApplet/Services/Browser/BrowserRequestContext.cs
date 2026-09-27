using System.Net;
using System.Net.Http;

namespace DownloadManagerApplet.Services.Browser;

/// <summary>
/// Browser headers and sign-in cookies for one taken-over download. Held in memory only: never saved to state.json or logged.
/// The cookie container keeps each cookie scoped to its own domain and path, including across redirects.
/// </summary>
public sealed class BrowserRequestContext
{
    private readonly CookieContainer _cookies = new();

    public string? Referrer { get; private init; }
    public string? UserAgent { get; private init; }
    public int CookieCount { get; private set; }

    public static BrowserRequestContext Create(BrowserHandoff handoff, bool useCookies, ILoggingService log, DateTimeOffset? now = null)
    {
        var context = new BrowserRequestContext
        {
            Referrer = BrowserHandoffPolicy.TryGetHttpUri(handoff.Referrer, out var referrer) ? referrer.AbsoluteUri : null,
            UserAgent = string.IsNullOrWhiteSpace(handoff.UserAgent) ? null : handoff.UserAgent.Trim()
        };

        if (useCookies && handoff.Cookies is { Count: > 0 } cookies)
        {
            var nowSeconds = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
            var skipped = 0;
            foreach (var cookie in cookies)
            {
                if (!context.TryAdd(cookie, nowSeconds))
                {
                    skipped++;
                }
            }

            if (skipped > 0)
            {
                log.Debug($"Skipped {skipped} of {cookies.Count} browser cookie(s) the HTTP stack can't represent");
            }
        }

        return context;
    }

    private bool TryAdd(BrowserCookie source, long nowSeconds)
    {
        if (source.ExpirationDate is { } expires && expires <= nowSeconds)
        {
            return false;
        }

        var domain = source.Domain.Trim();
        var bareDomain = domain.TrimStart('.');
        if (bareDomain.Length == 0)
        {
            return false;
        }

        try
        {
            var path = string.IsNullOrEmpty(source.Path) ? "/" : source.Path;
            if (source.HostOnly)
            {
                // An explicit Domain is domain-wide in CookieContainer; an implicit one (from the URI) stays exact-host.
                var hostCookie = new Cookie(source.Name, source.Value ?? string.Empty, path) { Secure = source.Secure };
                _cookies.Add(new Uri($"https://{bareDomain}/"), hostCookie);
            }
            else
            {
                _cookies.Add(new Cookie(source.Name, source.Value ?? string.Empty, path, "." + bareDomain) { Secure = source.Secure });
            }

            CookieCount++;
            return true;
        }
        catch (Exception ex) when (ex is CookieException or UriFormatException)
        {
            return false;
        }
    }

    /// <summary>A client that sends this context's cookies. Dispose it when the transfer ends.</summary>
    public HttpClient CreateClient(TimeSpan? timeout = null)
    {
        var handler = new SocketsHttpHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
            AllowAutoRedirect = true
        };

        var client = new HttpClient(handler, disposeHandler: true);
        if (timeout is { } limit)
        {
            client.Timeout = limit;
        }

        return client;
    }

    public void Apply(HttpRequestMessage request)
    {
        if (Referrer is not null)
        {
            request.Headers.Referrer = new Uri(Referrer);
        }

        if (UserAgent is not null)
        {
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        }
    }

    /// <summary>Test hook: the Cookie header this context would send to <paramref name="uri"/>.</summary>
    internal string GetCookieHeader(Uri uri) => _cookies.GetCookieHeader(uri);
}
