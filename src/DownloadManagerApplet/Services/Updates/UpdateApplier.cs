using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace DownloadManagerApplet.Services.Updates;

public enum UpdateOutcome
{
    Installed,
    Canceled,
    Failed
}

/// <summary>Result handed from the helper to the relaunched app.</summary>
public sealed record AfterUpdateInfo(UpdateOutcome Outcome, Version TargetVersion, int ExitCode, string? Detail)
{
    public const string Flag = "--after-update";

    public IEnumerable<string> ToArguments()
    {
        yield return Flag;
        yield return Outcome.ToString();
        yield return UpdateSignatureFormat.NormalizeVersion(TargetVersion);
        yield return ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Parses and removes the after-update arguments; returns null when absent or malformed.</summary>
    public static AfterUpdateInfo? Extract(List<string> args)
    {
        var i = args.IndexOf(Flag);
        if (i < 0)
        {
            return null;
        }

        var count = Math.Min(4, args.Count - i);
        var parts = args.GetRange(i, count);
        args.RemoveRange(i, count);

        if (count == 4
            && Enum.TryParse<UpdateOutcome>(parts[1], ignoreCase: false, out var outcome)
            && Version.TryParse(parts[2], out var version)
            && int.TryParse(parts[3], System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var code))
        {
            return new AfterUpdateInfo(outcome, version, code, null);
        }

        return null;
    }
}

/// <summary>Helper mode (<c>--apply-update</c>): waits for the app to exit, re-verifies and runs the installer, then relaunches the app.</summary>
public sealed class UpdateApplier
{
    public const string Flag = "--apply-update";

    private static readonly TimeSpan AppExitTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan InstallerTimeout = TimeSpan.FromMinutes(30);

    private readonly ILoggingService _log;
    private readonly UpdateSignatureVerifier _verifier;
    private readonly Version _currentVersion;

    public UpdateApplier(ILoggingService log, UpdateSignatureVerifier verifier, Version currentVersion)
    {
        _log = log;
        _verifier = verifier;
        _currentVersion = currentVersion;
    }

    public static IEnumerable<string> BuildArguments(VerifiedUpdate update, int waitPid, string appExe, string logDirectory) =>
    [
        Flag,
        "--installer", update.InstallerPath,
        "--sig", update.SignaturePath,
        "--version", UpdateSignatureFormat.NormalizeVersion(update.Manifest.Version),
        "--wait-pid", waitPid.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "--app", appExe,
        "--log-dir", logDirectory
    ];

    public sealed record Request(string InstallerPath, string SignaturePath, Version Version, int WaitPid, string AppExe, string LogDirectory);

    public static Request? ParseRequest(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] != Flag || args.Count % 2 != 1)
        {
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Count; i += 2)
        {
            values[args[i]] = args[i + 1];
        }

        return values.TryGetValue("--installer", out var installer)
            && values.TryGetValue("--sig", out var sig)
            && values.TryGetValue("--version", out var v) && Version.TryParse(v, out var version)
            && values.TryGetValue("--wait-pid", out var p) && int.TryParse(p, out var pid)
            && values.TryGetValue("--app", out var app)
            && values.TryGetValue("--log-dir", out var logDir)
            ? new Request(installer, sig, version, pid, app, logDir)
            : null;
    }

    /// <summary>Runs the whole apply sequence. Returns the helper's process exit code.</summary>
    public int Run(Request request)
    {
        _log.Info($"Update helper: applying {request.Version} (current {UpdateSignatureFormat.NormalizeVersion(_currentVersion)})");

        if (!WaitForAppExit(request.WaitPid))
        {
            _log.Error($"Update helper: app (pid {request.WaitPid}) did not exit within {AppExitTimeout.TotalSeconds:0}s; update not started");
            return 1;
        }

        var info = Apply(request);
        Relaunch(request.AppExe, info);
        return info.Outcome == UpdateOutcome.Failed ? 1 : 0;
    }

    private AfterUpdateInfo Apply(Request request)
    {
        if (request.Version <= _currentVersion)
        {
            _log.Error($"Update helper: refusing {request.Version}; not newer than {_currentVersion}");
            return new AfterUpdateInfo(UpdateOutcome.Failed, request.Version, 0, "downgrade refused");
        }

        FileStream guard;
        try
        {
            // Held open with FileShare.Read until the installer exits, so the verified file cannot be swapped before elevation.
            guard = new FileStream(request.InstallerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("Update helper: could not open the installer", ex);
            return new AfterUpdateInfo(UpdateOutcome.Failed, request.Version, 0, ex.Message);
        }

        using (guard)
        {
            try
            {
                var manifest = _verifier.VerifyManifest(File.ReadAllBytes(request.SignaturePath), request.Version, Path.GetFileName(request.InstallerPath));
                UpdateSignatureVerifier.VerifyContent(guard, manifest);
            }
            catch (Exception ex) when (ex is UpdateSignatureException or IOException or UnauthorizedAccessException)
            {
                _log.Error($"Update helper: installer failed verification: {ex.Message}");
                return new AfterUpdateInfo(UpdateOutcome.Failed, request.Version, 0, ex.Message);
            }

            var installLog = Path.Combine(request.LogDirectory, $"install-{UpdateSignatureFormat.NormalizeVersion(request.Version)}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            var psi = new ProcessStartInfo(request.InstallerPath)
            {
                UseShellExecute = true,
                Arguments = $"/exebasicui /exelog \"{installLog}\"",
                WorkingDirectory = Path.GetDirectoryName(request.InstallerPath)!
            };

            try
            {
                using var installer = Process.Start(psi);
                if (installer is null)
                {
                    return new AfterUpdateInfo(UpdateOutcome.Failed, request.Version, 0, "installer did not start");
                }

                _log.Info($"Update helper: installer started (pid {installer.Id}); log {installLog}");
                if (!installer.WaitForExit(InstallerTimeout))
                {
                    _log.Error($"Update helper: installer still running after {InstallerTimeout.TotalMinutes:0} min; relaunching anyway");
                    return new AfterUpdateInfo(UpdateOutcome.Failed, request.Version, 0, "installer timed out");
                }

                var code = installer.ExitCode;
                var outcome = code switch
                {
                    0 or 3010 or 1641 => UpdateOutcome.Installed,
                    1602 or -1 => UpdateOutcome.Canceled,
                    _ => UpdateOutcome.Failed
                };
                if (outcome == UpdateOutcome.Failed)
                {
                    _log.Error($"Update helper: installer exited {code}; see {installLog}");
                }
                else
                {
                    _log.Info($"Update helper: installer exited {code} ({outcome})");
                }

                return new AfterUpdateInfo(outcome, request.Version, code, null);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                _log.Info("Update helper: UAC prompt declined");
                return new AfterUpdateInfo(UpdateOutcome.Canceled, request.Version, 1223, "UAC declined");
            }
            catch (Win32Exception ex)
            {
                _log.Error("Update helper: could not start the installer", ex);
                return new AfterUpdateInfo(UpdateOutcome.Failed, request.Version, ex.NativeErrorCode, ex.Message);
            }
        }
    }

    private bool WaitForAppExit(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.WaitForExit(AppExitTimeout);
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            _log.Debug($"Update helper: pid {pid} not waitable ({ex.Message}); assuming exited");
            return true;
        }
    }

    private void Relaunch(string appExe, AfterUpdateInfo info)
    {
        var psi = new ProcessStartInfo(appExe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(appExe)!
        };
        foreach (var arg in info.ToArguments())
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            using var _ = Process.Start(psi);
            _log.Info($"Update helper: relaunched {appExe} ({info.Outcome})");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _log.Error($"Update helper: could not relaunch {appExe}", ex);
        }
    }
}
