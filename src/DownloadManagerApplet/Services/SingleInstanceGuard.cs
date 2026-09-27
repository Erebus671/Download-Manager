namespace DownloadManagerApplet.Services;

/// <summary>Owns the single-instance mutex for the process lifetime. Dispose on the thread that created it.</summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private bool _disposed;

    public bool IsPrimary { get; }

    /// <exception cref="UnauthorizedAccessException">Another account owns an object with this name.</exception>
    public SingleInstanceGuard(string mutexName, ILoggingService log)
    {
        _mutex = new Mutex(initiallyOwned: false, mutexName);
        try
        {
            IsPrimary = _mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            // Previous owner died without releasing; ownership transfers to us.
            log.Warn("Single-instance mutex was abandoned by a prior process; taking ownership");
            IsPrimary = true;
        }

        log.Debug($"Single-instance mutex '{mutexName}': {(IsPrimary ? "primary" : "secondary")}");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (IsPrimary)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not owned by this thread; closing the handle below still frees it at process exit.
            }
        }

        _mutex.Dispose();
    }
}
