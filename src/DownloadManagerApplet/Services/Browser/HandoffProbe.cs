using System.Net.Http;
using System.Net.Http.Headers;

namespace DownloadManagerApplet.Services.Browser;

/// <summary>Result of checking that the app can fetch a URL itself before the browser gives it up.</summary>
public sealed record ProbeResult(bool Ok, long? TotalBytes, string Detail);

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
            return new ProbeResult(true, total, $"server answered {(int)response.StatusCode}");
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
