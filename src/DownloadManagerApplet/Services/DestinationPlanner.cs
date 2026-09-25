using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using DownloadManagerApplet.Models;

namespace DownloadManagerApplet.Services;

/// <summary>
/// Chooses where a download is saved: a first guess from the URL when it is added, then a final
/// name and folder once the server's response headers (filename, size, type) are known.
/// </summary>
public sealed class DestinationPlanner
{
    private const int MaxFileNameLength = 180;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private readonly AppState _state;
    private readonly ILoggingService _log;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<Dispatcher?> _uiDispatcher;

    /// <param name="uiDispatcher">Where item changes are applied; defaults to the application's dispatcher. Tests pass one returning null to run inline.</param>
    public DestinationPlanner(AppState state, ILoggingService log, Func<DateTimeOffset>? clock = null, Func<Dispatcher?>? uiDispatcher = null)
    {
        _state = state;
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _uiDispatcher = uiDispatcher ?? (() => Application.Current?.Dispatcher);
    }

    /// <summary>Raised (on the UI thread when there is one) after a download's name or folder changed.</summary>
    public event Action<DownloadItem>? DestinationChanged;

    /// <summary>Builds a new item. A null <paramref name="folder"/> means Automatic: rules, then file type.</summary>
    public DownloadItem CreateItem(string url, Uri uri, string? folder, DownloadSource source = DownloadSource.Manual)
    {
        var fileName = DeriveFileName(uri, _clock());
        var facts = new DownloadFacts(uri, fileName, Source: source);
        var category = DestinationResolver.Categorize(_state.Settings, fileName);
        string? ruleName = null;
        var automatic = folder is null;

        if (folder is null)
        {
            var choice = DestinationResolver.Resolve(_state.Settings, facts, _clock(), _log);
            folder = choice.Folder;
            ruleName = choice.RuleName;
        }

        return new DownloadItem
        {
            Url = url,
            FileName = MakeUniqueFileName(folder, fileName, null),
            DestinationFolder = folder,
            AutoFolder = automatic,
            ResolveOnResponse = true,
            Category = category,
            RuleName = ruleName,
            Source = source
        };
    }

    /// <summary>
    /// Called by the engine once response headers arrive for a download starting from byte 0.
    /// Adopts the server's filename and, for Automatic downloads, re-picks the folder with the size and type now known.
    /// </summary>
    public void ApplyResponse(DownloadItem item, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!item.ResolveOnResponse)
        {
            return;
        }

        var mime = response.Content.Headers.ContentType?.MediaType;
        var size = response.Content.Headers.ContentLength;
        var serverName = ServerFileName(item, response);

        void Apply()
        {
            if (!item.ResolveOnResponse)
            {
                return;
            }

            item.ResolveOnResponse = false;
            item.MimeType = mime;

            var fileName = item.UserNamed ? item.FileName : serverName ?? item.FileName;
            var folder = item.DestinationFolder;
            if (!Uri.TryCreate(item.Url, UriKind.Absolute, out var uri))
            {
                return;
            }

            var facts = new DownloadFacts(uri, fileName, size, mime, item.Source);
            item.Category = DestinationResolver.Categorize(_state.Settings, fileName, mime);
            if (item.AutoFolder)
            {
                var choice = DestinationResolver.Resolve(_state.Settings, facts, _clock(), _log);
                folder = choice.Folder;
                item.RuleName = choice.RuleName;
            }

            var changed = !string.Equals(fileName, item.FileName, StringComparison.Ordinal)
                          || !string.Equals(folder, item.DestinationFolder, StringComparison.OrdinalIgnoreCase);
            if (changed)
            {
                var unique = MakeUniqueFileName(folder, fileName, item);
                _log.Info($"{item.FileName}: saving as {Path.Combine(folder, unique)}");
                item.FileName = unique;
                item.DestinationFolder = folder;
            }

            DestinationChanged?.Invoke(item);
        }

        var dispatcher = _uiDispatcher();
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Apply();
            return;
        }

        try
        {
            dispatcher.Invoke(Apply, DispatcherPriority.Normal, cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Dispatcher shutting down: keep the first-guess name rather than failing the download.
            _log.Debug($"{item.FileName}: UI shut down before the final name was applied");
        }
    }

    /// <summary>Renames a download at any stage except Canceled. Call on the UI thread. Returns null on success, else a message for the user.</summary>
    public string? Rename(DownloadItem item, string requested)
    {
        var name = SanitizeFileName(requested);
        if (name is null || !string.Equals(name, requested.Trim(), StringComparison.Ordinal))
        {
            return "That name isn't allowed. Avoid \\ / : * ? \" < > | and reserved names like CON.";
        }

        lock (item)
        {
            if (item.Status == DownloadStatus.Canceled)
            {
                return "Canceled downloads can't be renamed.";
            }

            if (string.Equals(name, item.FileName, StringComparison.Ordinal))
            {
                return null;
            }

            var caseOnly = string.Equals(name, item.FileName, StringComparison.OrdinalIgnoreCase);
            if (!caseOnly && IsTaken(item.DestinationFolder, name, item))
            {
                return $"\"{name}\" already exists in {item.DestinationFolder}.";
            }

            var oldName = item.FileName;
            if (item.Status == DownloadStatus.Completed)
            {
                var from = item.FullPath;
                var to = Path.Combine(item.DestinationFolder, name);
                try
                {
                    File.Move(from, to);
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    _log.Debug($"Rename skipped, {from} is gone: {ex.Message}");
                    return "The file is no longer in its folder.";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _log.Warn($"Could not rename {from} to {to}: {ex.Message}");
                    return $"Could not rename the file: {ex.Message}";
                }
            }
            else
            {
                // The engine may be writing the .part; leave it in place and rename on completion.
                item.PartFileName ??= oldName + ".part";
            }

            item.FileName = name;
            item.UserNamed = true;
            item.Category = DestinationResolver.Categorize(_state.Settings, name, item.MimeType);
            _log.Info($"Renamed {oldName} to {name}");
        }

        DestinationChanged?.Invoke(item);
        return null;
    }

    /// <summary>A name not used by a file, a .part file, or another download in <paramref name="folder"/>.</summary>
    public string MakeUniqueFileName(string folder, string fileName, DownloadItem? self)
    {
        var candidate = fileName;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var suffix = 1;

        while (IsTaken(folder, candidate, self))
        {
            candidate = $"{stem} ({suffix}){ext}";
            suffix++;
        }

        return candidate;
    }

    public static string DeriveFileName(Uri uri, DateTimeOffset now)
    {
        var name = SanitizeFileName(Path.GetFileName(uri.LocalPath));
        return name ?? $"download-{now:yyyyMMdd-HHmmss}";
    }

    /// <summary>A safe file name, or null when nothing usable is left.</summary>
    public static string? SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        // Drop any path a server tries to smuggle in ("..\..\evil.exe" becomes "evil.exe").
        var trimmed = name.Trim().Trim('"').Replace('/', '\\');
        trimmed = trimmed[(trimmed.LastIndexOf('\\') + 1)..];

        var invalid = Path.GetInvalidFileNameChars();
        var chars = trimmed.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray();
        var result = new string(chars).Trim().TrimEnd('.', ' ');
        if (result.Length == 0 || result is "." or "..")
        {
            return null;
        }

        if (ReservedNames.Contains(Path.GetFileNameWithoutExtension(result)))
        {
            result = "_" + result;
        }

        if (result.Length > MaxFileNameLength)
        {
            var ext = Path.GetExtension(result);
            ext = ext.Length > 20 ? string.Empty : ext;
            result = result[..(MaxFileNameLength - ext.Length)].TrimEnd('.', ' ') + ext;
        }

        return result;
    }

    private static string? ServerFileName(DownloadItem item, HttpResponseMessage response)
    {
        var disposition = response.Content.Headers.ContentDisposition;
        var fromHeader = SanitizeFileName(disposition?.FileNameStar) ?? SanitizeFileName(disposition?.FileName);
        if (fromHeader is not null)
        {
            return fromHeader;
        }

        // No header: after a redirect the final URL often carries the real name ("/latest" -> "/app-1.2.zip").
        if (Path.HasExtension(item.FileName))
        {
            return null;
        }

        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is null)
        {
            return null;
        }

        var redirected = SanitizeFileName(Path.GetFileName(finalUri.LocalPath));
        return redirected is not null && Path.HasExtension(redirected) ? redirected : null;
    }

    private bool IsTaken(string folder, string candidate, DownloadItem? self)
    {
        var path = Path.Combine(folder, candidate);
        var ownPart = self is not null && string.Equals(self.PartFilePath, path + ".part", StringComparison.OrdinalIgnoreCase);
        if (File.Exists(path) || (!ownPart && File.Exists(path + ".part")))
        {
            return true;
        }

        return _state.Downloads.Any(d =>
            !ReferenceEquals(d, self) &&
            string.Equals(d.DestinationFolder, folder, StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(d.FileName, candidate, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(d.PartFileName, candidate + ".part", StringComparison.OrdinalIgnoreCase)));
    }
}
