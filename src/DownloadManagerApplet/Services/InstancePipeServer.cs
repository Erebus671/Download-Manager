using System.IO;
using System.IO.Pipes;

namespace DownloadManagerApplet.Services;

/// <summary>
/// Accepts <see cref="InstanceMessage"/>s from other processes of the same user.
/// <see cref="PipeOptions.CurrentUserOnly"/> restricts the pipe ACL to the current account.
/// </summary>
public sealed class InstancePipeServer : IAsyncDisposable
{
    public static readonly TimeSpan DefaultConnectionTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(3);

    private readonly string _pipeName;
    private readonly Func<InstanceMessage, CancellationToken, Task<bool>> _handler;
    private readonly ILoggingService _log;
    private readonly TimeSpan _connectionTimeout;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public InstancePipeServer(
        string pipeName,
        Func<InstanceMessage, CancellationToken, Task<bool>> handler,
        ILoggingService log,
        TimeSpan? connectionTimeout = null)
    {
        _pipeName = pipeName;
        _handler = handler;
        _log = log;
        _connectionTimeout = connectionTimeout ?? DefaultConnectionTimeout;
    }

    public void Start()
    {
        if (_loop is not null)
        {
            throw new InvalidOperationException("Server already started.");
        }

        _loop = Task.Run(() => ListenLoopAsync(_stop.Token));
        _log.Info("Instance IPC server started");
    }

    private async Task ListenLoopAsync(CancellationToken stopToken)
    {
        while (!stopToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await pipe.WaitForConnectionAsync(stopToken);
                await HandleConnectionAsync(pipe, stopToken);
            }
            catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
            {
                break;
            }
            catch (UnauthorizedAccessException ex)
            {
                _log.Error($"Instance IPC pipe '{_pipeName}' is held by another account; retrying", ex);
                await DelayQuietly(FailureBackoff, stopToken);
            }
            catch (Exception ex)
            {
                _log.Error("Instance IPC server failure; restarting listener", ex);
                await DelayQuietly(FailureBackoff, stopToken);
            }
        }

        _log.Debug("Instance IPC server stopped");
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken stopToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
        timeout.CancelAfter(_connectionTimeout);

        try
        {
            var message = await InstanceMessageCodec.ReadAsync(pipe, timeout.Token);
            _log.Debug($"Instance IPC received {InstanceMessageCodec.Describe(message)}");

            var accepted = await _handler(message, timeout.Token);
            pipe.WriteByte(accepted ? InstanceMessageCodec.Accepted : InstanceMessageCodec.Rejected);
            await pipe.FlushAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!stopToken.IsCancellationRequested)
        {
            _log.Warn($"Instance IPC client timed out after {_connectionTimeout.TotalSeconds:0}s");
        }
        catch (InvalidDataException ex)
        {
            _log.Warn($"Instance IPC rejected malformed message: {ex.Message}");
            TryReply(pipe, InstanceMessageCodec.Rejected);
        }
        catch (IOException ex)
        {
            _log.Warn($"Instance IPC client disconnected: {ex.Message}");
        }
    }

    private static void TryReply(NamedPipeServerStream pipe, byte reply)
    {
        try
        {
            pipe.WriteByte(reply);
            pipe.Flush();
        }
        catch (IOException)
        {
        }
    }

    private static async Task DelayQuietly(TimeSpan delay, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(ShutdownTimeout);
            }
            catch (TimeoutException)
            {
                _log.Warn("Instance IPC server did not stop within the shutdown timeout");
            }
        }

        _stop.Dispose();
    }
}
