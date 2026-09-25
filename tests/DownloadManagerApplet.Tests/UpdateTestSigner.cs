using System.Security.Cryptography;
using System.Text.Json;
using DownloadManagerApplet.Services.Updates;

namespace DownloadManagerApplet.Tests;

/// <summary>Ephemeral P-256 key that signs update payloads the same way tools/ReleaseSigning does.</summary>
internal sealed class UpdateTestSigner : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public string PublicKey => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

    public string KeyId => UpdateSignatureFormat.KeyIdOf(_key.ExportSubjectPublicKeyInfo());

    public UpdateSignatureVerifier Verifier() => new([PublicKey]);

    public byte[] Sign(byte[] content, string version, string fileName = GitHubReleaseSource.InstallerAssetName) =>
        SignRaw(version, fileName, content.Length, Convert.ToHexStringLower(SHA256.HashData(content)));

    public byte[] SignRaw(string version, string fileName, long size, string sha256)
    {
        var signature = _key.SignData(
            UpdateSignatureFormat.BuildPayload(version, fileName, size, sha256),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        var file = new UpdateSignatureFile(UpdateSignatureFormat.CurrentFormat, KeyId, version, fileName, size, sha256, Convert.ToBase64String(signature));
        return JsonSerializer.SerializeToUtf8Bytes(file, UpdateSignatureFormat.JsonOptions);
    }

    public void Dispose() => _key.Dispose();
}
