using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

namespace DownloadManagerApplet.Services.Browser;

/// <summary>Result of checking that the app can fetch a URL itself before the browser gives it up.</summary>
/// <param name="HeaderFileName">Sanitized Content-Disposition name.</param>
/// <param name="RedirectFileName">Sanitized name from the final URL after redirects, when it has an extension.</param>
public sealed record ProbeResult(bool Ok, long? TotalBytes, string Detail, string? HeaderFileName = null, string? RedirectFileName = null);

public interface IHandoffProbe
{
    Task<ProbeResult> ProbeAsync(BrowserHandoff handoff, BrowserRequestContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Requests the first byte with the browser's cookies and headers. Refused or slow URLs (POST-only, expired sign-in,
/// one-time links) stay in the browser instead of failing in the app after the browser copy is gone.
/// </summary>
public sealed class HttpHandoffProbe : IHandoffProbe
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    private readonly TimeSpan _timeout;

    public HttpHandoffProbe(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? DefaultTimeout;
    }

    public async Task<ProbeResult> ProbeAsync(BrowserHandoff handoff, BrowserRequestContext context, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);
        using var client = context.CreateClient(System.Threading.Timeout.InfiniteTimeSpan);
        using var request = new HttpRequestMessage(HttpMethod.Get, handoff.Url);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        context.Apply(request);

        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new ProbeResult(false, null, $"server answered {(int)response.StatusCode}");
            }

            var total = response.Content.Headers.ContentRange?.Length
                        ?? (response.StatusCode == System.Net.HttpStatusCode.OK ? response.Content.Headers.ContentLength : null);
            var disposition = response.Content.Headers.ContentDisposition;
            var headerName = DestinationPlanner.SanitizeFileName(disposition?.FileNameStar) ?? DestinationPlanner.SanitizeFileName(disposition?.FileName);
            var finalUri = response.RequestMessage?.RequestUri;
            var redirectName = finalUri is null ? null : DestinationPlanner.SanitizeFileName(Path.GetFileName(finalUri.LocalPath));
            return new ProbeResult(true, total, $"server answered {(int)response.StatusCode}", headerName,
                redirectName is not null && Path.HasExtension(redirectName) ? redirectName : null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProbeResult(false, null, $"no answer within {_timeout.TotalSeconds:0}s");
        }
        catch (HttpRequestException ex)
        {
            return new ProbeResult(false, null, ex.Message);
        }
    }
}
