using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using DownloadManagerApplet.Models;
using DownloadManagerApplet.Mvvm;
using DownloadManagerApplet.Services;
using DownloadManagerApplet.Services.Browser;

namespace DownloadManagerApplet.ViewModels;

public enum BrowserRowState
{
    Connected,
    NotConnected,
    NotDetected
}

public sealed class BrowserStatusRow : ObservableObject
{
    private BrowserRowState _state;
    private string _statusText = string.Empty;
    private string _detail = string.Empty;

    public BrowserStatusRow(string name, IReadOnlyList<string> browsers, string notDetectedHint)
    {
        Name = name;
        Browsers = browsers;
        NotDetectedHint = notDetectedHint;
    }

    public string Name { get; }
    public IReadOnlyList<string> Browsers { get; }
    public string NotDetectedHint { get; }

    public BrowserRowState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(ShowGetExtension));
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public string Detail
    {
        get => _detail;
        set => SetProperty(ref _detail, value);
    }

    public bool ShowGetExtension => State != BrowserRowState.Connected;
}

public sealed record StartAppOption(bool Value, string Label);

public sealed class ExcludedSiteRow
{
    public ExcludedSiteRow(string site, Action<ExcludedSiteRow> remove)
    {
        Site = site;
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public string Site { get; }
    public RelayCommand RemoveCommand { get; }
}

/// <summary>Settings > Browser integration. Edits the live settings; the shared Save Settings button persists them.</summary>
public sealed class BrowserIntegrationViewModel : ObservableObject
{
    /// <summary>The extension checks in at least every 5 minutes while its browser runs (background.js HEALTHY_POLL_MS).</summary>
    public static readonly TimeSpan ConnectedWindow = TimeSpan.FromMinutes(15);

    /// <summary>How often the page re-evaluates connection ages and the connector registration.</summary>
    public static readonly TimeSpan StatusRefreshInterval = TimeSpan.FromSeconds(30);
    private const long BytesPerMegabyte = 1024 * 1024;

    private readonly AppState _state;
    private readonly NativeHostRegistration? _registration;
    private readonly ILoggingService _log;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<string> _openUrl;

    private string _minimumMegabytesText;
    private string _newSite = string.Empty;
    private string? _siteError;
    private bool _hostRegistered;
    private string _hostStatusText = string.Empty;

    public BrowserIntegrationViewModel(
        AppState state,
        NativeHostRegistration? registration,
        ILoggingService log,
        Func<DateTimeOffset>? clock = null,
        Action<string>? openUrl = null)
    {
        _state = state;
        _registration = registration;
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _openUrl = openUrl ?? OpenInBrowser;
        _minimumMegabytesText = FormatMegabytes(Settings.MinimumBytes);

        Browsers =
        [
            new BrowserStatusRow("Google Chrome", ["Chrome"], "Install the extension, then open Chrome once"),
            new BrowserStatusRow("Microsoft Edge", ["Edge"], "Install the extension, then open Edge once"),
            new BrowserStatusRow("Mozilla Firefox", ["Firefox"], "Install the extension, then open Firefox once"),
            new BrowserStatusRow("Brave / Opera / Vivaldi", ["Brave", "Opera", "Vivaldi", "Chromium"], "Use the Chrome extension")
        ];

        AddSiteCommand = new RelayCommand(AddSite);
        RepairCommand = new RelayCommand(Repair, () => _registration is not null);
        GetExtensionCommand = new RelayCommand(() => _openUrl(BrowserExtensionIds.GetExtensionUrl));

        RefreshFromSettings();
        RefreshConnections();
        RefreshHostStatus();
    }

    private BrowserIntegrationSettings Settings => _state.Settings.BrowserIntegration;

    public IReadOnlyList<BrowserStatusRow> Browsers { get; }
    public ObservableCollection<ExcludedSiteRow> ExcludedSites { get; } = new();

    public IReadOnlyList<StartAppOption> StartAppOptions { get; } =
    [
        new(true, "Start it and take over"),
        new(false, "Let the browser download")
    ];

    public bool Enabled
    {
        get => Settings.Enabled;
        set
        {
            if (Settings.Enabled != value)
            {
                Settings.Enabled = value;
                OnPropertyChanged();
            }
        }
    }

    public bool StartAppWhenNotRunning
    {
        get => Settings.StartAppWhenNotRunning;
        set
        {
            if (Settings.StartAppWhenNotRunning != value)
            {
                Settings.StartAppWhenNotRunning = value;
                OnPropertyChanged();
            }
        }
    }

    public bool UseBrowserCookies
    {
        get => Settings.UseBrowserCookies;
        set
        {
            if (Settings.UseBrowserCookies != value)
            {
                Settings.UseBrowserCookies = value;
                OnPropertyChanged();
            }
        }
    }

    public bool ShowBrowserNotification
    {
        get => Settings.ShowBrowserNotification;
        set
        {
            if (Settings.ShowBrowserNotification != value)
            {
                Settings.ShowBrowserNotification = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Applied by <see cref="ApplyPendingEdits"/> when Save Settings is pressed.</summary>
    public string MinimumMegabytesText
    {
        get => _minimumMegabytesText;
        set => SetProperty(ref _minimumMegabytesText, value);
    }

    public string NewSite
    {
        get => _newSite;
        set => SetProperty(ref _newSite, value);
    }

    public string? SiteError
    {
        get => _siteError;
        private set => SetProperty(ref _siteError, value);
    }

    public bool HostRegistered
    {
        get => _hostRegistered;
        private set => SetProperty(ref _hostRegistered, value);
    }

    public string HostStatusText
    {
        get => _hostStatusText;
        private set => SetProperty(ref _hostStatusText, value);
    }

    public RelayCommand AddSiteCommand { get; }
    public RelayCommand RepairCommand { get; }
    public RelayCommand GetExtensionCommand { get; }

    /// <summary>Validates and applies text fields. Null when valid, else the message for the save line.</summary>
    public string? ApplyPendingEdits()
    {
        var text = MinimumMegabytesText.Trim();
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var megabytes) ||
            megabytes < 0 || megabytes * BytesPerMegabyte > BrowserHandoffPolicy.MaxMinimumBytes)
        {
            return $"Take-over size must be a number of MB from 0 to {BrowserHandoffPolicy.MaxMinimumBytes / BytesPerMegabyte}.";
        }

        Settings.MinimumBytes = (long)Math.Round(megabytes * BytesPerMegabyte);
        MinimumMegabytesText = FormatMegabytes(Settings.MinimumBytes);
        return null;
    }

    /// <summary>After a successful save: turning the feature on (re)registers the connector.</summary>
    public void AfterSave()
    {
        if (Settings.Enabled && _registration is not null && !HostRegistered)
        {
            _registration.Register();
        }

        RefreshHostStatus();
    }

    /// <summary>Re-reads settings the extension may have changed.</summary>
    public void RefreshFromSettings()
    {
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(StartAppWhenNotRunning));
        OnPropertyChanged(nameof(UseBrowserCookies));
        OnPropertyChanged(nameof(ShowBrowserNotification));

        ExcludedSites.Clear();
        foreach (var site in Settings.ExcludedSites)
        {
            ExcludedSites.Add(new ExcludedSiteRow(site, RemoveSite));
        }
    }

    public void RefreshConnections()
    {
        var now = _clock();
        foreach (var row in Browsers)
        {
            var seen = _state.BrowserConnections
                .Where(c => row.Browsers.Contains(c.Browser, StringComparer.OrdinalIgnoreCase))
                .OrderByDescending(c => c.LastSeen)
                .FirstOrDefault();

            if (seen is null)
            {
                row.State = BrowserRowState.NotDetected;
                row.StatusText = "Not detected";
                row.Detail = row.NotDetectedHint;
                continue;
            }

            var version = string.IsNullOrEmpty(seen.ExtensionVersion) ? "Extension" : $"Extension {seen.ExtensionVersion}";
            var which = row.Browsers.Count > 1 ? $"{seen.Browser} · " : string.Empty;
            if (now - seen.LastSeen <= ConnectedWindow)
            {
                row.State = BrowserRowState.Connected;
                row.StatusText = "Connected";
                row.Detail = $"{which}{version} · last seen {Ago(now - seen.LastSeen)}";
            }
            else
            {
                row.State = BrowserRowState.NotConnected;
                row.StatusText = "Not connected";
                row.Detail = $"{which}Seen {Ago(now - seen.LastSeen)} · browser closed or extension disabled";
            }
        }
    }

    public void RefreshHostStatus()
    {
        if (_registration is null)
        {
            HostRegistered = false;
            HostStatusText = "unavailable in this build";
            return;
        }

        var status = _registration.GetStatus();
        HostRegistered = status.Registered;
        HostStatusText = status.Detail;
    }

    private void Repair()
    {
        var problem = _registration?.Register();
        RefreshHostStatus();
        if (problem is not null)
        {
            HostStatusText = problem;
        }
    }

    private void AddSite()
    {
        var pattern = BrowserHandoffPolicy.NormalizeSitePattern(NewSite);
        if (pattern is null)
        {
            SiteError = "Enter a site like example.com or *.example.com.";
            return;
        }

        SiteError = null;
        NewSite = string.Empty;
        if (Settings.ExcludedSites.Contains(pattern))
        {
            return;
        }

        Settings.ExcludedSites.Add(pattern);
        ExcludedSites.Add(new ExcludedSiteRow(pattern, RemoveSite));
        _log.Debug($"Excluded site added: {pattern}");
    }

    private void RemoveSite(ExcludedSiteRow row)
    {
        Settings.ExcludedSites.Remove(row.Site);
        ExcludedSites.Remove(row);
        _log.Debug($"Excluded site removed: {row.Site}");
    }

    internal static string FormatMegabytes(long bytes) =>
        (bytes / (decimal)BytesPerMegabyte).ToString("0.##", CultureInfo.CurrentCulture);

    internal static string Ago(TimeSpan elapsed) => elapsed switch
    {
        { TotalMinutes: < 1 } => "just now",
        { TotalMinutes: < 60 } => $"{(int)elapsed.TotalMinutes} min ago",
        { TotalHours: < 24 } => $"{(int)elapsed.TotalHours} h ago",
        { TotalDays: < 2 } => "yesterday",
        _ => $"{(int)elapsed.TotalDays} days ago"
    };

    private void OpenInBrowser(string url)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _log.Warn($"Could not open {url}: {ex.Message}");
        }
    }
}
