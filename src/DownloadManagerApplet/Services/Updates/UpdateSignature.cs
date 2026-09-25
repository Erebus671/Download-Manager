using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DownloadManagerApplet.Services.Updates;

/// <summary>Contents of a release's <c>.sig</c> asset, as written by tools/ReleaseSigning.</summary>
public sealed record UpdateSignatureFile(int Format, string KeyId, string Version, string File, long Size, string Sha256, string Signature);

/// <summary>A signature whose metadata has been authenticated; the file itself is checked separately.</summary>
public sealed record SignedUpdateManifest(string KeyId, Version Version, string FileName, long Size, string Sha256);

public sealed class UpdateSignatureException(string message) : Exception(message);

/// <summary>Payload layout must stay identical to BuildPayload in tools/ReleaseSigning/ReleaseSigning.cs.</summary>
public static class UpdateSignatureFormat
{
    public const int CurrentFormat = 1;
    public const int MaxSignatureFileBytes = 16 * 1024;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static byte[] BuildPayload(string version, string fileName, long size, string sha256) =>
        Encoding.UTF8.GetBytes($"ATDS-UPDATE-SIG-V1\nversion={version}\nfile={fileName}\nsize={size}\nsha256={sha256}");

    public static string KeyIdOf(byte[] subjectPublicKeyInfo) =>
        Convert.ToHexStringLower(SHA256.HashData(subjectPublicKeyInfo))[..16];

    public static string NormalizeVersion(Version v) => new Version(v.Major, v.Minor, Math.Max(v.Build, 0)).ToString(3);
}

/// <summary>ECDSA P-256 verification of update installers against embedded trusted keys. Fails closed.</summary>
public sealed class UpdateSignatureVerifier
{
    private readonly IReadOnlyDictionary<string, byte[]> _trustedKeys;

    /// <param name="trustedPublicKeys">Base64 SubjectPublicKeyInfo values.</param>
    public UpdateSignatureVerifier(IEnumerable<string> trustedPublicKeys)
    {
        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var b64 in trustedPublicKeys)
        {
            var spki = Convert.FromBase64String(b64);
            using var probe = ECDsa.Create();
            probe.ImportSubjectPublicKeyInfo(spki, out _);
            if (probe.KeySize != 256)
            {
                throw new ArgumentException($"Trusted update key must be P-256, found {probe.KeySize}-bit");
            }

            keys[UpdateSignatureFormat.KeyIdOf(spki)] = spki;
        }

        _trustedKeys = keys;
    }

    public bool HasTrustedKeys => _trustedKeys.Count > 0;

    /// <summary>Authenticates the signature file and checks it names the expected release and asset.</summary>
    /// <exception cref="UpdateSignatureException">Malformed, untrusted, invalid, or for a different release.</exception>
    public SignedUpdateManifest VerifyManifest(ReadOnlySpan<byte> signatureFileBytes, Version expectedVersion, string expectedFileName)
    {
        if (signatureFileBytes.Length > UpdateSignatureFormat.MaxSignatureFileBytes)
        {
            throw new UpdateSignatureException($"Signature file is {signatureFileBytes.Length} bytes; limit is {UpdateSignatureFormat.MaxSignatureFileBytes}");
        }

        UpdateSignatureFile sig;
        try
        {
            sig = JsonSerializer.Deserialize<UpdateSignatureFile>(signatureFileBytes, UpdateSignatureFormat.JsonOptions)
                ?? throw new UpdateSignatureException("Signature file is empty");
        }
        catch (JsonException ex)
        {
            throw new UpdateSignatureException($"Signature file is malformed: {ex.Message}");
        }

        if (sig.Format != UpdateSignatureFormat.CurrentFormat)
        {
            throw new UpdateSignatureException($"Unsupported signature format {sig.Format}");
        }

        if (sig.KeyId is null || !_trustedKeys.TryGetValue(sig.KeyId, out var spki))
        {
            throw new UpdateSignatureException($"Signed by untrusted key '{sig.KeyId}'");
        }

        if (sig.Version is null || sig.File is null || sig.Sha256 is null || sig.Signature is null)
        {
            throw new UpdateSignatureException("Signature file is missing required fields");
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(sig.Signature);
        }
        catch (FormatException)
        {
            throw new UpdateSignatureException("Signature is not valid base64");
        }

        using (var key = ECDsa.Create())
        {
            key.ImportSubjectPublicKeyInfo(spki, out _);
            var payload = UpdateSignatureFormat.BuildPayload(sig.Version, sig.File, sig.Size, sig.Sha256);
            if (!key.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                throw new UpdateSignatureException("Signature does not match");
            }
        }

        // Metadata is authenticated from here on.
        var expected = UpdateSignatureFormat.NormalizeVersion(expectedVersion);
        if (!string.Equals(sig.Version, expected, StringComparison.Ordinal))
        {
            throw new UpdateSignatureException($"Signature is for version {sig.Version}, release is {expected}");
        }

        if (!string.Equals(sig.File, expectedFileName, StringComparison.Ordinal))
        {
            throw new UpdateSignatureException($"Signature is for '{sig.File}', asset is '{expectedFileName}'");
        }

        if (sig.Size <= 0 || sig.Sha256.Length != 64)
        {
            throw new UpdateSignatureException("Signed size or hash is invalid");
        }

        return new SignedUpdateManifest(sig.KeyId, Version.Parse(sig.Version), sig.File, sig.Size, sig.Sha256);
    }

    /// <summary>Checks the stream's length and SHA-256 against the manifest. Reads from the current position to the end.</summary>
    /// <exception cref="UpdateSignatureException">Size or hash differs.</exception>
    public static void VerifyContent(Stream content, SignedUpdateManifest manifest)
    {
        if (content.CanSeek && content.Length != manifest.Size)
        {
            throw new UpdateSignatureException($"Installer is {content.Length} bytes; signed size is {manifest.Size}");
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        if (!string.Equals(hash, manifest.Sha256, StringComparison.Ordinal))
        {
            throw new UpdateSignatureException("Installer hash does not match the signature");
        }
    }
}
