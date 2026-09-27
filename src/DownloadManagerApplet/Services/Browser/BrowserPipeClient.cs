using System.IO;
using System.IO.Pipes;

namespace DownloadManagerApplet.Services.Browser;

public enum BrowserPipeStatus
{
    Ok,

    /// <summary>No app listening (not running, or still starting).</summary>
    Unreachable,

    /// <summary>Connected, but the exchange failed or timed out.</summary>
    Failed
}

public sealed record BrowserPipeResult(BrowserPipeStatus Status, BrowserResponse? Response, string? Error);

/// <summary>Native host side of the named pipe. Linked into the native host; no app dependencies.</summary>
public static class BrowserPipeClient
{
    public static readonly TimeSpan DefaultReplyTimeout = TimeSpan.FromSeconds(10);

    public static async Task<BrowserPipeResult> SendAsync(
        string pipeName,
        BrowserRequest request,
        TimeSpan connectTimeout,
        TimeSpan? replyTimeout = null,
        CancellationToken cancellationToken = default)
    {
        NamedPipeClientStream pipe;
        try
        {
            pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new BrowserPipeResult(BrowserPipeStatus.Failed, null, ex.Message);
        }

        await using (pipe)
        {
            try
            {
                await pipe.ConnectAsync((int)connectTimeout.TotalMilliseconds, cancellationToken);
            }
            catch (TimeoutException)
            {
                return new BrowserPipeResult(BrowserPipeStatus.Unreachable, null, "The app is not running.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // UnauthorizedAccess: CurrentUserOnly found a pipe owned by another account.
                return new BrowserPipeResult(BrowserPipeStatus.Unreachable, null, ex.Message);
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(replyTimeout ?? DefaultReplyTimeout);
            try
            {
                await InstanceMessageCodec.WriteAsync(pipe, InstanceMessage.FromBrowser(request), cts.Token);
                var response = await InstanceMessageCodec.ReadBrowserReplyAsync(pipe, cts.Token);
                return new BrowserPipeResult(BrowserPipeStatus.Ok, response, null);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new BrowserPipeResult(BrowserPipeStatus.Failed, null, "The app did not answer in time.");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                return new BrowserPipeResult(BrowserPipeStatus.Failed, null, ex.Message);
            }
        }
    }
}
