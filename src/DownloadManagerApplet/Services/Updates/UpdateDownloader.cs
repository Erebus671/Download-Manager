using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace DownloadManagerApplet.Services.Updates;

public sealed record VerifiedUpdate(ReleaseInfo Release, SignedUpdateManifest Manifest, string InstallerPath, string SignaturePath);

public interface IUpdateDownloader
{
    Task<VerifiedUpdate> DownloadAsync(ReleaseInfo release, IProgress<double>? progress, CancellationToken cancellationToken);

    /// <summary>Removes cached updates other than <paramref name="keep"/>, and the helper copy.</summary>
    void CleanUp(Version? keep);
}

/// <summary>Fetches a release's signature, authenticates it, then streams the installer into the updates folder and checks its hash.</summary>
public sealed class UpdateDownloader : IUpdateDownloader
{
    public const long MaxInstallerBytes = 500L * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly UpdateSignatureVerifier _verifier;
    private readonly string _updatesRoot;
    private readonly ILoggingService _log;

    public UpdateDownloader(HttpClient http, UpdateSignatureVerifier verifier, string updatesRoot, ILoggingService log)
    {
        _http = http;
        _verifier = verifier;
        _updatesRoot = updatesRoot;
        _log = log;
    }

    public string UpdatesRoot => _updatesRoot;

    /// <exception cref="UpdateSignatureException">Signature or content failed verification; nothing is left on disk.</exception>
    /// <exception cref="UpdateCheckException">Network or file-system failure.</exception>
    public async Task<VerifiedUpdate> DownloadAsync(ReleaseInfo release, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (!_verifier.HasTrustedKeys)
        {
            throw new UpdateSignatureException("This build has no trusted update keys");
        }

        var folder = Path.Combine(_updatesRoot, UpdateSignatureFormat.NormalizeVersion(release.Version));
        var installerPath = Path.Combine(folder, release.Installer.Name);
        var sigPath = Path.Combine(folder, release.Signature.Name);

        if (TryReuse(release, installerPath, sigPath) is { } cached)
        {
            _log.Info($"Update {release.Version}: reusing verified download in {folder}");
            progress?.Report(100);
            return cached;
        }

        try
        {
            Directory.CreateDirectory(folder);

            var sigBytes = await DownloadSmallAsync(release.Signature.DownloadUrl, UpdateSignatureFormat.MaxSignatureFileBytes, cancellationToken).ConfigureAwait(false);
            var manifest = _verifier.VerifyManifest(sigBytes, release.Version, release.Installer.Name);
            _log.Debug($"Update {release.Version}: signature metadata verified (key {manifest.KeyId}, {manifest.Size} bytes)");

            if (manifest.Size > MaxInstallerBytes)
            {
                throw new UpdateSignatureException($"Signed installer size {manifest.Size} exceeds the {MaxInstallerBytes} byte limit");
            }

            if (release.Installer.Size != manifest.Size)
            {
                throw new UpdateSignatureException($"Release asset is {release.Installer.Size} bytes; signed size is {manifest.Size}");
            }

            await DownloadInstallerAsync(release.Installer.DownloadUrl, installerPath, manifest, progress, cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(sigPath, sigBytes, cancellationToken).ConfigureAwait(false);

            _log.Info($"Update {release.Version}: downloaded and verified ({manifest.Size} bytes)");
            return new VerifiedUpdate(release, manifest, installerPath, sigPath);
        }
        catch (Exception ex) when (ex is UpdateSignatureException or UpdateCheckException or OperationCanceledException)
        {
            DeleteFolder(folder);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DeleteFolder(folder);
            throw new UpdateCheckException($"Could not save the update: {ex.Message}", ex);
        }
        catch (HttpRequestException ex)
        {
            DeleteFolder(folder);
            throw new UpdateCheckException($"Could not download the update: {ex.Message}", ex);
        }
    }

    public void CleanUp(Version? keep)
    {
        if (!Directory.Exists(_updatesRoot))
        {
            return;
        }

        var keepName = keep is null ? null : UpdateSignatureFormat.NormalizeVersion(keep);
        foreach (var dir in Directory.EnumerateDirectories(_updatesRoot))
        {
            var name = Path.GetFileName(dir);
            if (name == keepName)
            {
                continue;
            }

            // The helper that relaunched this process may still be exiting; retried next launch.
            DeleteFolder(dir, expectInUse: name == UpdateLauncher.HelperFolderName);
        }
    }

    private VerifiedUpdate? TryReuse(ReleaseInfo release, string installerPath, string sigPath)
    {
        if (!File.Exists(installerPath) || !File.Exists(sigPath))
        {
            return null;
        }

        try
        {
            var manifest = _verifier.VerifyManifest(File.ReadAllBytes(sigPath), release.Version, release.Installer.Name);
            using var fs = new FileStream(installerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            UpdateSignatureVerifier.VerifyContent(fs, manifest);
            return new VerifiedUpdate(release, manifest, installerPath, sigPath);
        }
        catch (Exception ex) when (ex is UpdateSignatureException or IOException or UnauthorizedAccessException)
        {
            _log.Warn($"Update {release.Version}: cached download failed re-verification ({ex.Message}); downloading again");
            DeleteFolder(Path.GetDirectoryName(installerPath)!);
            return null;
        }
    }

    private async Task<byte[]> DownloadSmallAsync(Uri url, int maxBytes, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new UpdateCheckException($"Signature download returned HTTP {(int)response.StatusCode}");
        }

        return await GitHubReleaseSource.ReadCappedAsync(response.Content, maxBytes, cancellationToken).ConfigureAwait(false);
    }

    private async Task DownloadInstallerAsync(Uri url, string destination, SignedUpdateManifest manifest, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var partPath = destination + ".part";
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new UpdateCheckException($"Installer download returned HTTP {(int)response.StatusCode}");
        }

        if (response.Content.Headers.ContentLength is { } declared && declared != manifest.Size)
        {
            throw new UpdateSignatureException($"Server reports {declared} bytes; signed size is {manifest.Size}");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var target = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            long total = 0;
            var lastReported = -1;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > manifest.Size)
                {
                    throw new UpdateSignatureException($"Installer download exceeded the signed size of {manifest.Size} bytes");
                }

                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

                var percent = (int)(total * 100 / manifest.Size);
                if (percent != lastReported)
                {
                    lastReported = percent;
                    progress?.Report(percent);
                }
            }

            if (total != manifest.Size)
            {
                throw new UpdateSignatureException($"Installer download ended at {total} bytes; signed size is {manifest.Size}");
            }
        }

        var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (!string.Equals(actual, manifest.Sha256, StringComparison.Ordinal))
        {
            throw new UpdateSignatureException("Installer hash does not match the signature");
        }

        File.Move(partPath, destination, overwrite: true);
    }

    private void DeleteFolder(string folder, bool expectInUse = false)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (expectInUse)
            {
                _log.Debug($"Update helper folder still in use: {ex.Message}");
            }
            else
            {
                _log.Warn($"Could not delete update folder {folder}: {ex.Message}");
            }
        }
    }
}
