using System.IO;
using System.Text.Json;

namespace DownloadManagerApplet.Services;

public sealed class JsonAppStore : IAppStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly ILoggingService _log;
    private readonly object _fileLock = new();

    public JsonAppStore(string filePath, ILoggingService log)
    {
        _filePath = filePath;
        _log = log;
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
    }

    public AppState Load()
    {
        lock (_fileLock)
        {
            if (!File.Exists(_filePath))
            {
                return new AppState();
            }

            try
            {
                var json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<AppState>(json, SerializerOptions) ?? new AppState();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _log.Error($"Failed to load application state from {_filePath}; starting with a fresh state", ex);
                return new AppState();
            }
        }
    }

    public void Save(AppState state)
    {
        lock (_fileLock)
        {
            var tempPath = _filePath + ".tmp";
            try
            {
                var json = JsonSerializer.Serialize(state, SerializerOptions);
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _filePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Error($"Failed to save application state to {_filePath}", ex);
            }
        }
    }
}
