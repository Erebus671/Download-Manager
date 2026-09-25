using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using DownloadManagerApplet.Models;

namespace DownloadManagerApplet.Services;

/// <summary>
/// Coordinates concurrent downloads: enforces the configured concurrency limit, retries
/// transient failures with backoff, and distinguishes a user pause (keeps the .part file
/// for resume) from a user cancel (deletes it).
/// </summary>
public sealed class DownloadOrchestrator
{
    private static readonly TimeSpan SlotPollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan RetryBaseDelay = TimeSpan.FromSeconds(2);

    private readonly IDownloadEngine _engine;
    private readonly ILoggingService _log;
    private readonly Func<int> _getMaxConcurrent;
    private readonly Func<int> _getMaxRetryAttempts;

    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _tokens = new();
    private readonly ConcurrentDictionary<Guid, bool> _cancelRequested = new();
    private int _activeCount;
    private volatile bool _suspended;

    /// <summary>Raised whenever an item's status changes, so the caller can persist state.</summary>
    public event Action? StateChanged;

    public DownloadOrchestrator(
        IDownloadEngine engine,
        ILoggingService log,
        Func<int> getMaxConcurrent,
        Func<int> getMaxRetryAttempts)
    {
        _engine = engine;
        _log = log;
        _getMaxConcurrent = getMaxConcurrent;
        _getMaxRetryAttempts = getMaxRetryAttempts;
    }

    public void Enqueue(DownloadItem item, IProgress<DownloadProgress> progress)
    {
        item.Status = DownloadStatus.Queued;
        item.LastError = null;
        _cancelRequested[item.Id] = false;
        StateChanged?.Invoke();

        _ = ProcessItemAsync(item, progress);
    }

    public void Pause(DownloadItem item)
    {
        if (_tokens.TryGetValue(item.Id, out var cts))
        {
            _cancelRequested[item.Id] = false;
            cts.Cancel();
        }
    }

    public void Cancel(DownloadItem item)
    {
        _cancelRequested[item.Id] = true;
        if (_tokens.TryGetValue(item.Id, out var cts))
        {
            cts.Cancel();
        }
        else
        {
            // Not currently running (e.g. still Queued waiting for a slot): finalize immediately.
            DeletePartFile(item);
            item.Status = DownloadStatus.Canceled;
            StateChanged?.Invoke();
        }
    }

    /// <summary>Pauses every running download and holds queued ones; waits until none are active. Returns false on timeout.</summary>
    public async Task<bool> SuspendAsync(TimeSpan timeout)
    {
        _suspended = true;
        foreach (var (id, cts) in _tokens)
        {
            _cancelRequested[id] = false;
            cts.Cancel();
        }

        var deadline = DateTime.UtcNow + timeout;
        while (Volatile.Read(ref _activeCount) > 0 || !_tokens.IsEmpty)
        {
            if (DateTime.UtcNow >= deadline)
            {
                _log.Warn($"Suspend timed out with {Volatile.Read(ref _activeCount)} download(s) still active");
                return false;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        return true;
    }

    public void Unsuspend() => _suspended = false;

    public void Resume(DownloadItem item, IProgress<DownloadProgress> progress)
    {
        if (item.Status is DownloadStatus.Paused or DownloadStatus.Error or DownloadStatus.Canceled)
        {
            item.RetryCount = 0;
            item.LastError = null;
            Enqueue(item, progress);
        }
    }

    private async Task ProcessItemAsync(DownloadItem item, IProgress<DownloadProgress> progress)
    {
        if (!await AcquireSlotAsync().ConfigureAwait(false))
        {
            HoldForSuspend(item);
            return;
        }

        try
        {
            if (_suspended)
            {
                HoldForSuspend(item);
                return;
            }

            using var cts = new CancellationTokenSource();
            _tokens[item.Id] = cts;
            if (_suspended)
            {
                // SuspendAsync may have enumerated _tokens before this registration.
                cts.Cancel();
            }

            item.Status = DownloadStatus.Downloading;
            StateChanged?.Invoke();

            var maxRetries = _getMaxRetryAttempts();
            while (true)
            {
                try
                {
                    await _engine.DownloadAsync(item, progress, cts.Token).ConfigureAwait(false);
                    _log.Info($"{item.FileName}: download completed");
                    break;
                }
                catch (OperationCanceledException)
                {
                    if (_cancelRequested.TryGetValue(item.Id, out var wasCanceled) && wasCanceled)
                    {
                        DeletePartFile(item);
                        item.Status = DownloadStatus.Canceled;
                        _log.Info($"{item.FileName}: canceled by user");
                    }
                    else
                    {
                        item.Status = DownloadStatus.Paused;
                        _log.Info($"{item.FileName}: paused by user at {item.BytesReceived} bytes");
                    }

                    break;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException)
                {
                    item.RetryCount++;
                    item.LastError = ex.Message;

                    if (item.RetryCount > maxRetries)
                    {
                        item.Status = DownloadStatus.Error;
                        _log.Error($"{item.FileName}: giving up after {item.RetryCount} attempts", ex);
                        break;
                    }

                    _log.Warn($"{item.FileName}: transient failure (attempt {item.RetryCount}/{maxRetries}): {ex.Message}");
                    var delay = TimeSpan.FromSeconds(RetryBaseDelay.TotalSeconds * item.RetryCount);
                    await Task.Delay(delay).ConfigureAwait(false);
                }
            }

            _tokens.TryRemove(item.Id, out _);
            StateChanged?.Invoke();
        }
        finally
        {
            ReleaseSlot();
        }
    }

    private void HoldForSuspend(DownloadItem item)
    {
        item.Status = DownloadStatus.Paused;
        _log.Debug($"{item.FileName}: held as Paused while downloads are suspended");
        StateChanged?.Invoke();
    }

    /// <summary>False when downloads were suspended while waiting; no slot is held in that case.</summary>
    private async Task<bool> AcquireSlotAsync()
    {
        while (true)
        {
            if (_suspended)
            {
                return false;
            }

            if (Interlocked.Increment(ref _activeCount) <= Math.Max(1, _getMaxConcurrent()))
            {
                return true;
            }

            Interlocked.Decrement(ref _activeCount);
            await Task.Delay(SlotPollInterval).ConfigureAwait(false);
        }
    }

    private void ReleaseSlot() => Interlocked.Decrement(ref _activeCount);

    private static void DeletePartFile(DownloadItem item)
    {
        try
        {
            if (File.Exists(item.PartFilePath))
            {
                File.Delete(item.PartFilePath);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; a leftover .part file is harmless and can be removed manually.
        }
    }
}
