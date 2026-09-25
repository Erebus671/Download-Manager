using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DownloadManagerApplet.Services.Updates;
using DotNetTestKit;

namespace DownloadManagerApplet.Tests;

public class UpdateSignatureTests : TestBase
{
    private const string Name = GitHubReleaseSource.InstallerAssetName;
    private static readonly byte[] Content = Encoding.UTF8.GetBytes("pretend installer bytes");
    private static readonly Version V110 = new(1, 1, 0);

    [Fact]
    public void VerifyManifest_AcceptsValidSignature()
    {
        using var signer = new UpdateTestSigner();

        var manifest = signer.Verifier().VerifyManifest(signer.Sign(Content, "1.1.0"), V110, Name);

        Assert.Equal(V110, manifest.Version);
        Assert.Equal(Content.Length, manifest.Size);
        Assert.Equal(signer.KeyId, manifest.KeyId);
        UpdateSignatureVerifier.VerifyContent(new MemoryStream(Content), manifest);
    }

    [Fact]
    public void VerifyManifest_RejectsUntrustedKey()
    {
        using var signer = new UpdateTestSigner();
        using var other = new UpdateTestSigner();

        var ex = Assert.Throws<UpdateSignatureException>(() => other.Verifier().VerifyManifest(signer.Sign(Content, "1.1.0"), V110, Name));
        Assert.Contains("untrusted", ex.Message);
    }

    [Fact]
    public void VerifyManifest_RejectsTamperedMetadata()
    {
        using var signer = new UpdateTestSigner();
        var sig = JsonSerializer.Deserialize<UpdateSignatureFile>(signer.Sign(Content, "1.1.0"), UpdateSignatureFormat.JsonOptions)!;
        var tampered = JsonSerializer.SerializeToUtf8Bytes(sig with { Size = sig.Size + 1 }, UpdateSignatureFormat.JsonOptions);

        var ex = Assert.Throws<UpdateSignatureException>(() => signer.Verifier().VerifyManifest(tampered, V110, Name));
        Assert.Contains("does not match", ex.Message);
    }

    [Fact]
    public void VerifyManifest_RejectsSignatureForOtherVersion()
    {
        using var signer = new UpdateTestSigner();

        Assert.Throws<UpdateSignatureException>(() => signer.Verifier().VerifyManifest(signer.Sign(Content, "1.0.9"), V110, Name));
    }

    [Fact]
    public void VerifyManifest_RejectsSignatureForOtherFile()
    {
        using var signer = new UpdateTestSigner();

        Assert.Throws<UpdateSignatureException>(() => signer.Verifier().VerifyManifest(signer.Sign(Content, "1.1.0", "other.exe"), V110, Name));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"format\":1,\"keyId\":\"x\",\"extra\":true}")]
    public void VerifyManifest_RejectsMalformedFile(string json)
    {
        using var signer = new UpdateTestSigner();

        Assert.Throws<UpdateSignatureException>(() => signer.Verifier().VerifyManifest(Encoding.UTF8.GetBytes(json), V110, Name));
    }

    [Fact]
    public void VerifyManifest_RejectsOversizeFile()
    {
        using var signer = new UpdateTestSigner();
        var big = new byte[UpdateSignatureFormat.MaxSignatureFileBytes + 1];

        Assert.Throws<UpdateSignatureException>(() => signer.Verifier().VerifyManifest(big, V110, Name));
    }

    [Fact]
    public void VerifyContent_RejectsModifiedInstaller()
    {
        using var signer = new UpdateTestSigner();
        var manifest = signer.Verifier().VerifyManifest(signer.Sign(Content, "1.1.0"), V110, Name);
        var modified = (byte[])Content.Clone();
        modified[0] ^= 0xFF;

        Assert.Throws<UpdateSignatureException>(() => UpdateSignatureVerifier.VerifyContent(new MemoryStream(modified), manifest));
        Assert.Throws<UpdateSignatureException>(() => UpdateSignatureVerifier.VerifyContent(new MemoryStream([.. Content, 0]), manifest));
    }

    [Fact]
    public void Verifier_WithNoKeys_HasNoTrustedKeys()
    {
        Assert.False(new UpdateSignatureVerifier([]).HasTrustedKeys);
    }

    [Fact]
    public void Verifier_RejectsNonP256Key()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);

        Assert.Throws<ArgumentException>(() => new UpdateSignatureVerifier([Convert.ToBase64String(p384.ExportSubjectPublicKeyInfo())]));
    }

    [Fact]
    public void TrustedUpdateKeys_AreValidP256KeysAndNotEmpty()
    {
        // Constructing the verifier validates every embedded key; a bad paste would fail here, not in the field.
        Assert.True(new UpdateSignatureVerifier(TrustedUpdateKeys.All).HasTrustedKeys);
    }

    [Theory]
    [InlineData(1, 1, "1.1.0")]
    [InlineData(2, 0, "2.0.0")]
    public void NormalizeVersion_PadsToThreeParts(int major, int minor, string expected)
    {
        Assert.Equal(expected, UpdateSignatureFormat.NormalizeVersion(new Version(major, minor)));
        Assert.Equal("1.2.3", UpdateSignatureFormat.NormalizeVersion(new Version(1, 2, 3, 4)));
    }
}
