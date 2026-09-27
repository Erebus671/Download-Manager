using System.Text.Json;
using DownloadManagerApplet.Models;

namespace DownloadManagerApplet.NativeHost;

public sealed record HostState(BrowserIntegrationSettings Settings, HostLogLevel LogLevel, string? Problem);

public interface IStateReader
{
    HostState Read();
}

/// <summary>Reads the app's state.json (read-only) for when the app is closed. Missing or unreadable state means defaults.</summary>
public sealed class StateFileReader : IStateReader
{
    private const long MaxStateBytes = 64L * 1024 * 1024;
    private const int Attempts = 3;

    private readonly string _path;

    public StateFileReader(string path)
    {
        _path = path;
    }

    public HostState Read()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return ReadOnce();
            }
            catch (IOException) when (attempt < Attempts)
            {
                // The app replaces state.json atomically; a read can land mid-swap.
                Thread.Sleep(50);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Defaults($"state.json unreadable: {ex.Message}");
            }
        }
    }

    private HostState ReadOnce()
    {
        if (!File.Exists(_path))
        {
            return Defaults(null);
        }

        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaxStateBytes)
        {
            return Defaults($"state.json is {stream.Length} bytes; ignored");
        }

        try
        {
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("Settings", out var settingsElement) || settingsElement.ValueKind != JsonValueKind.Object)
            {
                return Defaults(null);
            }

            var settings = settingsElement.TryGetProperty("BrowserIntegration", out var browserElement) && browserElement.ValueKind == JsonValueKind.Object
                ? browserElement.Deserialize<BrowserIntegrationSettings>() ?? new BrowserIntegrationSettings()
                : new BrowserIntegrationSettings();
            settings.ExcludedSites ??= [];

            var level = HostLogLevel.Info;
            if (settingsElement.TryGetProperty("MinimumLogLevel", out var levelElement))
            {
                level = ParseLevel(levelElement);
            }

            return new HostState(settings, level, null);
        }
        catch (JsonException ex)
        {
            return Defaults($"state.json is not valid JSON: {ex.Message}");
        }
    }

    private static HostLogLevel ParseLevel(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number when element.TryGetInt32(out var number) && Enum.IsDefined((HostLogLevel)number) => (HostLogLevel)number,
        JsonValueKind.String when Enum.TryParse<HostLogLevel>(element.GetString(), ignoreCase: true, out var named) => named,
        _ => HostLogLevel.Info
    };

    private static HostState Defaults(string? problem) => new(new BrowserIntegrationSettings(), HostLogLevel.Info, problem);
}
