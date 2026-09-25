using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace DownloadManagerApplet.Services.Updates;

/// <summary>Starts the update helper: a copy of this app, run from outside the install folder so the installer can replace every file.</summary>
public static class UpdateLauncher
{
    public const string HelperFolderName = "helper";

    /// <exception cref="UpdateCheckException">The helper could not be prepared or started.</exception>
    public static void StartHelper(VerifiedUpdate update, string updatesRoot, string logDirectory, ILoggingService log)
    {
        var appExe = Environment.ProcessPath ?? throw new UpdateCheckException("Process path is unavailable");
        var appDir = Path.GetDirectoryName(appExe)!;
        var helperDir = Path.Combine(updatesRoot, HelperFolderName);

        try
        {
            if (Directory.Exists(helperDir))
            {
                Directory.Delete(helperDir, recursive: true);
            }

            Directory.CreateDirectory(helperDir);
            var stem = Path.GetFileNameWithoutExtension(appExe);
            foreach (var file in Directory.EnumerateFiles(appDir, stem + ".*"))
            {
                File.Copy(file, Path.Combine(helperDir, Path.GetFileName(file)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UpdateCheckException($"Could not prepare the update helper: {ex.Message}", ex);
        }

        var helperExe = Path.Combine(helperDir, Path.GetFileName(appExe));
        var psi = new ProcessStartInfo(helperExe)
        {
            UseShellExecute = false,
            WorkingDirectory = helperDir
        };
        foreach (var arg in UpdateApplier.BuildArguments(update, Environment.ProcessId, appExe, logDirectory))
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(psi) ?? throw new UpdateCheckException("Update helper did not start");
            log.Info($"Update {update.Manifest.Version}: helper started (pid {process.Id}); exiting for install");
        }
        catch (Win32Exception ex)
        {
            throw new UpdateCheckException($"Could not start the update helper: {ex.Message}", ex);
        }
    }
}
