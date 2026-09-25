using System.Diagnostics;
using System.Security.Principal;

namespace DownloadManagerApplet.Services;

/// <summary>Per-user, per-session instance identity. Pipe names are machine-global, so they carry the SID and session.</summary>
public static class InstanceNames
{
    private const string Base = "AtraTech.DownloadSolutions";

    private static readonly Lazy<string> Scope = new(() =>
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("Current user has no SID.");
        using var process = Process.GetCurrentProcess();
        return $"{sid}.{process.SessionId}";
    });

    public static string MutexName => $@"Local\{Base}.{Scope.Value}";
    public static string PipeName => $"{Base}.{Scope.Value}";
}

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
