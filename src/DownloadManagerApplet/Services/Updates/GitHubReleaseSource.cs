using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace DownloadManagerApplet.Services.Updates;

public sealed record ReleaseAsset(string Name, Uri DownloadUrl, long Size);

public sealed record ReleaseInfo(Version Version, string Tag, string Notes, bool IsPrerelease, ReleaseAsset Installer, ReleaseAsset Signature);

public sealed class UpdateCheckException(string message, Exception? inner = null) : Exception(message, inner);

public interface IReleaseSource
{
    /// <summary>Highest-versioned published release that has both the installer and its signature, or null.</summary>
    /// <exception cref="UpdateCheckException">Network, HTTP, or response-format failure.</exception>
    Task<ReleaseInfo?> GetLatestAsync(bool includePrerelease, CancellationToken cancellationToken);
}

public sealed class GitHubReleaseSource : IReleaseSource
{
    public const string InstallerAssetName = "AtraTechDownloadSolutions.exe";
    public const string SignatureAssetName = InstallerAssetName + ".sig";

    private const int MaxResponseBytes = 2 * 1024 * 1024;
    private const int MaxNotesChars = 4000;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly Uri _releasesUrl;
    private readonly ILoggingService _log;

    public GitHubReleaseSource(HttpClient http, string owner, string repo, ILoggingService log)
    {
        _http = http;
        _releasesUrl = new Uri($"https://api.github.com/repos/{owner}/{repo}/releases?per_page=20");
        _log = log;
    }

    public async Task<ReleaseInfo?> GetLatestAsync(bool includePrerelease, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _releasesUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        byte[] body;
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                throw new UpdateCheckException($"GitHub rate limit reached (HTTP {(int)response.StatusCode}); will retry later");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateCheckException($"GitHub returned HTTP {(int)response.StatusCode}");
            }

            body = await ReadCappedAsync(response.Content, MaxResponseBytes, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateCheckException($"GitHub did not respond within {RequestTimeout.TotalSeconds:0}s");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new UpdateCheckException($"Could not reach GitHub: {ex.Message}", ex);
        }

        try
        {
            return Parse(body, includePrerelease, _log);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new UpdateCheckException($"Unexpected GitHub response: {ex.Message}", ex);
        }
    }

    internal static ReleaseInfo? Parse(byte[] json, bool includePrerelease, ILoggingService log)
    {
        using var doc = JsonDocument.Parse(json);
        ReleaseInfo? best = null;

        foreach (var release in doc.RootElement.EnumerateArray())
        {
            var tag = release.GetProperty("tag_name").GetString() ?? string.Empty;
            if (release.GetProperty("draft").GetBoolean())
            {
                continue;
            }

            var prerelease = release.GetProperty("prerelease").GetBoolean();
            if (prerelease && !includePrerelease)
            {
                continue;
            }

            if (!TryParseTag(tag, out var version))
            {
                log.Debug($"Update check: skipping release tag '{tag}' (not vMAJOR.MINOR.PATCH)");
                continue;
            }

            if (best is not null && version <= best.Version)
            {
                continue;
            }

            ReleaseAsset? installer = null, signature = null;
            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString();
                if (name != InstallerAssetName && name != SignatureAssetName)
                {
                    continue;
                }

                if (!Uri.TryCreate(asset.GetProperty("browser_download_url").GetString(), UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
                {
                    log.Warn($"Update check: release {tag} asset '{name}' has a non-https URL; ignoring the release");
                    installer = signature = null;
                    break;
                }

                var parsed = new ReleaseAsset(name, url, asset.GetProperty("size").GetInt64());
                if (name == InstallerAssetName) installer = parsed; else signature = parsed;
            }

            if (installer is null || signature is null)
            {
                log.Debug($"Update check: release {tag} lacks {InstallerAssetName} or its .sig; skipping");
                continue;
            }

            var notes = release.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString()!.Trim() : string.Empty;
            if (notes.Length > MaxNotesChars)
            {
                notes = notes[..MaxNotesChars] + "...";
            }

            best = new ReleaseInfo(version, tag, notes, prerelease, installer, signature);
        }

        return best;
    }

    internal static bool TryParseTag(string tag, out Version version)
    {
        version = new Version(0, 0, 0);
        var raw = tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;
        if (!Version.TryParse(raw, out var parsed) || parsed.Build < 0 || parsed.Revision >= 0)
        {
            return false;
        }

        version = parsed;
        return true;
    }

    internal static async Task<byte[]> ReadCappedAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maxBytes)
        {
            throw new UpdateCheckException($"Response is {content.Headers.ContentLength} bytes; limit is {maxBytes}");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw new UpdateCheckException($"Response exceeds {maxBytes} bytes");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
