namespace DownloadManagerApplet.Models;

/// <summary>Settings > Browser integration. Also read from state.json by the native host when the app is closed.</summary>
public sealed class BrowserIntegrationSettings
{
    public const long DefaultMinimumBytes = 10L * 1024 * 1024;

    public bool Enabled { get; set; }

    /// <summary>Downloads smaller than this stay in the browser; unknown sizes are taken over.</summary>
    public long MinimumBytes { get; set; } = DefaultMinimumBytes;

    public bool StartAppWhenNotRunning { get; set; } = true;
    public bool UseBrowserCookies { get; set; } = true;
    public bool ShowBrowserNotification { get; set; } = true;

    /// <summary>"example.com" (the site and its subdomains) or "*.example.com" (subdomains only).</summary>
    public List<string> ExcludedSites { get; set; } = new();
}
