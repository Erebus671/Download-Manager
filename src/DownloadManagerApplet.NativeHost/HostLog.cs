using System.Globalization;
using System.Text;

namespace DownloadManagerApplet.NativeHost;

/// <summary>Levels match the app's LogLevelSetting values stored in state.json.</summary>
public enum HostLogLevel
{
    Debug,
    Info,
    Warn,
    Error,
    Critical,
    Fatal
}

public interface IHostLog
{
    HostLogLevel MinimumLevel { get; set; }
    void Write(HostLogLevel level, string message);
}

/// <summary>
/// Appends to logs\nativehost-yyyyMMdd.txt beside the app's logs. Several host processes can run at once, so each line
/// is one shared append. Never writes to stdout (that is the browser channel); failures go to stderr, which browsers log.
/// </summary>
public sealed class HostLog : IHostLog
{
    private readonly string _folder;

    public HostLog(string folder, HostLogLevel minimumLevel = HostLogLevel.Info)
    {
        _folder = folder;
        MinimumLevel = minimumLevel;
    }

    public HostLogLevel MinimumLevel { get; set; }

    public void Write(HostLogLevel level, string message)
    {
        if (level < MinimumLevel)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        var line = $"{now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)} [{level.ToString().ToUpperInvariant()}] [{Environment.ProcessId}] {message}{Environment.NewLine}";
        var path = Path.Combine(_folder, $"nativehost-{now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.txt");
        try
        {
            Directory.CreateDirectory(_folder);
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            var bytes = Encoding.UTF8.GetBytes(line);
            stream.Write(bytes, 0, bytes.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"DownloadSolutions native host: could not write log ({ex.Message}): {line.TrimEnd()}");
        }
    }
}

public static class HostLogExtensions
{
    public static void Debug(this IHostLog log, string message) => log.Write(HostLogLevel.Debug, message);
    public static void Info(this IHostLog log, string message) => log.Write(HostLogLevel.Info, message);
    public static void Warn(this IHostLog log, string message) => log.Write(HostLogLevel.Warn, message);
    public static void Error(this IHostLog log, string message) => log.Write(HostLogLevel.Error, message);
}
