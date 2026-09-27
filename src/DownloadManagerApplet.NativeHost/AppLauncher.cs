using System.ComponentModel;
using System.Runtime.InteropServices;
using DownloadManagerApplet.Services;

namespace DownloadManagerApplet.NativeHost;

public interface IAppLauncher
{
    /// <summary>True when an app instance holds the single-instance mutex.</summary>
    bool IsRunning();

    /// <summary>Null on success, else why the app could not be started.</summary>
    string? Launch(bool background);
}

/// <summary>
/// Starts DownloadManagerApplet.exe from the host's folder. Browsers run hosts inside a job object that may be killed
/// with the host, so the app is created with CREATE_BREAKAWAY_FROM_JOB, falling back when the job forbids breakaway.
/// </summary>
public sealed class AppLauncher : IAppLauncher
{
    public const string AppExeName = "DownloadManagerApplet.exe";

    private const uint CreateBreakawayFromJob = 0x01000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint DetachedProcess = 0x00000008;
    private const int ErrorAccessDenied = 5;

    private readonly string _appPath;
    private readonly IHostLog _log;

    public AppLauncher(string appPath, IHostLog log)
    {
        _appPath = appPath;
        _log = log;
    }

    public bool IsRunning()
    {
        try
        {
            if (Mutex.TryOpenExisting(InstanceNames.MutexName, out var mutex))
            {
                mutex.Dispose();
                return true;
            }

            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // Exists but not openable by us: still means an instance holds it.
            return true;
        }
    }

    public string? Launch(bool background)
    {
        if (!File.Exists(_appPath))
        {
            _log.Error($"App not found at {_appPath}");
            return "The app is missing from the install folder.";
        }

        var commandLine = background ? $"\"{_appPath}\" {InstanceNames.BackgroundFlag}" : $"\"{_appPath}\"";
        var flags = CreateUnicodeEnvironment | DetachedProcess;
        if (TryCreate(commandLine, flags | CreateBreakawayFromJob, out var error))
        {
            _log.Info($"Started the app{(background ? " in the background" : string.Empty)}");
            return null;
        }

        if (error == ErrorAccessDenied)
        {
            _log.Warn("Browser job forbids breakaway; starting the app inside it");
            if (TryCreate(commandLine, flags, out error))
            {
                _log.Info("Started the app (no breakaway)");
                return null;
            }
        }

        var message = new Win32Exception(error).Message;
        _log.Error($"Could not start the app ({error}): {message}");
        return $"Could not start the app: {message}";
    }

    private bool TryCreate(string commandLine, uint flags, out int error)
    {
        var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
        var mutableCommandLine = new char[commandLine.Length + 1];
        commandLine.CopyTo(0, mutableCommandLine, 0, commandLine.Length);

        if (!CreateProcessW(_appPath, mutableCommandLine, IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero,
                Path.GetDirectoryName(_appPath), ref startup, out var info))
        {
            error = Marshal.GetLastWin32Error();
            return false;
        }

        CloseHandle(info.hThread);
        CloseHandle(info.hProcess);
        error = 0;
        return true;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string lpApplicationName,
        char[] lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref StartupInfo lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
