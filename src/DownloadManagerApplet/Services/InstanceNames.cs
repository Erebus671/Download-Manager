using System.Diagnostics;
using System.Security.Principal;

namespace DownloadManagerApplet.Services;

/// <summary>Per-user, per-session instance identity. Pipe names are machine-global, so they carry the SID and session.</summary>
public static class InstanceNames
{
    private const string Base = "AtraTech.DownloadSolutions";

    /// <summary>Command-line flag: start minimized without taking focus (used by the browser connector).</summary>
    public const string BackgroundFlag = "--background";

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
