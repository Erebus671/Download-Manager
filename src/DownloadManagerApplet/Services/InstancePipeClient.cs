using System.IO;
using System.IO.Pipes;

namespace DownloadManagerApplet.Services;

public enum InstanceSendResult
{
    Accepted,
    Rejected,
    Unreachable
}

/// <summary>Hands a message to the running instance. Used by secondary launches, the native host, and the updater.</summary>
public static class InstancePipeClient
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Connect (retrying until <paramref name="timeout"/> while the primary starts up), send, await the reply.
    /// <see cref="PipeOptions.CurrentUserOnly"/> also verifies the server runs as the current user.
    /// </summary>
    public static async Task<InstanceSendResult> SendAsync(
        string pipeName,
        InstanceMessage message,
        ILoggingService log,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var limit = timeout ?? DefaultTimeout;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(limit);

        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            await pipe.ConnectAsync(cts.Token);
            GrantForeground(pipe, log);

            await InstanceMessageCodec.WriteAsync(pipe, message, cts.Token);

            var reply = new byte[1];
            var read = await pipe.ReadAsync(reply, cts.Token);
            if (read == 1 && reply[0] == InstanceMessageCodec.Accepted)
            {
                return InstanceSendResult.Accepted;
            }

            log.Warn($"Running instance rejected {InstanceMessageCodec.Describe(message)}");
            return InstanceSendResult.Rejected;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            log.Error($"Running instance did not respond within {limit.TotalSeconds:0}s");
            return InstanceSendResult.Unreachable;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            log.Error("Could not deliver message to the running instance", ex);
            return InstanceSendResult.Unreachable;
        }
    }

    // The launching process holds foreground rights; pass them on so the primary can bring its window forward.
    private static void GrantForeground(NamedPipeClientStream pipe, ILoggingService log)
    {
        if (!NativeMethods.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverPid) ||
            !NativeMethods.AllowSetForegroundWindow(serverPid))
        {
            log.Debug($"AllowSetForegroundWindow unavailable (Win32 error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
        }
    }
}
