using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace DownloadManagerApplet.Services.Browser;

public sealed record NativeHostStatus(bool Registered, string Detail);

/// <summary>
/// Registers the native host for the current user: writes the manifests under LocalAppData and points each
/// browser's HKCU NativeMessagingHosts key at them. Idempotent; re-run on startup so updates and moves self-heal.
/// </summary>
public sealed class NativeHostRegistration
{
    public const string HostExeName = "DownloadManagerApplet.NativeHost.exe";

    /// <summary>HKCU paths the browsers read. Opera and Vivaldi read Chrome's key.</summary>
    private static readonly (string Browser, string KeyPath, bool Firefox)[] Targets =
    [
        ("Chrome", @"Software\Google\Chrome\NativeMessagingHosts", false),
        ("Edge", @"Software\Microsoft\Edge\NativeMessagingHosts", false),
        ("Chromium", @"Software\Chromium\NativeMessagingHosts", false),
        ("Brave", @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts", false),
        ("Firefox", @"Software\Mozilla\NativeMessagingHosts", true)
    ];

    private readonly RegistryKey _root;
    private readonly string _manifestFolder;
    private readonly string _hostExePath;
    private readonly ILoggingService _log;

    /// <param name="root">HKCU in production; a scratch subkey in tests.</param>
    public NativeHostRegistration(RegistryKey root, string manifestFolder, string hostExePath, ILoggingService log)
    {
        _root = root;
        _manifestFolder = manifestFolder;
        _hostExePath = hostExePath;
        _log = log;
    }

    public string ChromiumManifestPath => Path.Combine(_manifestFolder, $"{BrowserProtocol.HostName}.chromium.json");
    public string FirefoxManifestPath => Path.Combine(_manifestFolder, $"{BrowserProtocol.HostName}.firefox.json");

    /// <summary>Null on success, else a message for the Settings page (the error is logged).</summary>
    public string? Register()
    {
        if (!File.Exists(_hostExePath))
        {
            _log.Error($"Native host not found at {_hostExePath}; browser integration can't connect. Reinstall the app.");
            return "The browser connector is missing from the install folder. Reinstall the app.";
        }

        try
        {
            Directory.CreateDirectory(_manifestFolder);
            WriteAtomic(ChromiumManifestPath, BuildChromiumManifest(_hostExePath));
            WriteAtomic(FirefoxManifestPath, BuildFirefoxManifest(_hostExePath));

            foreach (var target in Targets)
            {
                using var key = _root.CreateSubKey($@"{target.KeyPath}\{BrowserProtocol.HostName}", writable: true);
                key.SetValue(string.Empty, target.Firefox ? FirefoxManifestPath : ChromiumManifestPath, RegistryValueKind.String);
            }

            _log.Info($"Native host registered for Chrome, Edge, Chromium, Brave, and Firefox ({_hostExePath})");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _log.Error("Could not register the native host", ex);
            return $"Could not register the browser connector: {ex.Message}";
        }
    }

    public NativeHostStatus GetStatus()
    {
        try
        {
            if (!File.Exists(_hostExePath))
            {
                return new NativeHostStatus(false, "connector missing from install folder");
            }

            foreach (var target in Targets)
            {
                using var key = _root.OpenSubKey($@"{target.KeyPath}\{BrowserProtocol.HostName}");
                var manifest = key?.GetValue(string.Empty) as string;
                if (manifest is null || !File.Exists(manifest) || !ManifestPointsAt(manifest, _hostExePath))
                {
                    return new NativeHostStatus(false, $"not registered for {target.Browser}");
                }
            }

            return new NativeHostStatus(true, "registered");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or JsonException)
        {
            _log.Warn($"Could not read native host registration: {ex.Message}");
            return new NativeHostStatus(false, "status unavailable");
        }
    }

    internal static string BuildChromiumManifest(string hostExePath) => new JsonObject
    {
        ["name"] = BrowserProtocol.HostName,
        ["description"] = "AtraTech Download Solutions browser connector",
        ["path"] = hostExePath,
        ["type"] = "stdio",
        ["allowed_origins"] = new JsonArray(BrowserExtensionIds.Chromium.Select(id => (JsonNode)$"chrome-extension://{id}/").ToArray())
    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    internal static string BuildFirefoxManifest(string hostExePath) => new JsonObject
    {
        ["name"] = BrowserProtocol.HostName,
        ["description"] = "AtraTech Download Solutions browser connector",
        ["path"] = hostExePath,
        ["type"] = "stdio",
        ["allowed_extensions"] = new JsonArray((JsonNode)BrowserExtensionIds.Firefox)
    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    private static bool ManifestPointsAt(string manifestPath, string hostExePath)
    {
        var node = JsonNode.Parse(File.ReadAllText(manifestPath));
        var path = node?["path"]?.GetValue<string>();
        return path is not null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(hostExePath), StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteAtomic(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }
}
