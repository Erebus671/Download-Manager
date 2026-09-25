// Release signing for AtraTech Download Solutions updates.
// Run: dotnet run tools/ReleaseSigning/ReleaseSigning.cs -- <command> [options]
// Payload format must stay identical to UpdateSignatureFormat in the app.

#:property PublishAot=false

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

const int MinPasswordLength = 16;

try
{
    return args.Length == 0 ? Usage() : args[0] switch
    {
        "keygen" => KeyGen(Options.Parse(args[1..])),
        "sign" => Sign(Options.Parse(args[1..])),
        "verify" => Verify(Options.Parse(args[1..])),
        _ => Usage()
    };
}
catch (UsageException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 2;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException or FormatException)
{
    Console.Error.WriteLine($"error: {ex.GetType().Name}: {ex.Message}");
    return 1;
}

static int Usage()
{
    Console.Error.WriteLine("""
        Usage:
          keygen --out <key.pfx>
          sign   --key <key.pfx> --file <installer.exe> --version <x.y.z> [--password-stdin] [--force]
          verify --public-key <base64 or .pub.txt> --file <installer.exe> --version <x.y.z> [--sig <file.sig>]
        """);
    return 2;
}

static int KeyGen(Options o)
{
    var outPath = Path.GetFullPath(o.Required("--out"));
    if (File.Exists(outPath))
    {
        throw new UsageException($"{outPath} already exists; refusing to overwrite a signing key");
    }

    var password = o.Flag("--password-stdin") ? ReadPasswordFromStdin() : PromptNewPassword();

    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var request = new CertificateRequest("CN=AtraTech Update Signing", key, HashAlgorithmName.SHA256);
    using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(50));
    var pfx = cert.ExportPkcs12(Pkcs12ExportPbeParameters.Pbes2Aes256Sha256, password);

    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
    using (var fs = new FileStream(outPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
    {
        fs.Write(pfx);
    }

    var spki = key.ExportSubjectPublicKeyInfo();
    var keyId = KeyIdOf(spki);
    var publicKey = Convert.ToBase64String(spki);
    var pubPath = outPath + ".pub.txt";
    File.WriteAllText(pubPath, $"keyId={keyId}{Environment.NewLine}publicKey={publicKey}{Environment.NewLine}");

    Console.WriteLine($"Private key: {outPath}");
    Console.WriteLine($"Public key:  {pubPath}");
    Console.WriteLine($"keyId={keyId}");
    Console.WriteLine($"publicKey={publicKey}");
    Console.WriteLine("Back up the .pfx to offline media and store the password in your password manager.");
    return 0;
}

static int Sign(Options o)
{
    var keyPath = o.Required("--key");
    if (!File.Exists(keyPath))
    {
        throw new UsageException($"Key file not found: {keyPath}");
    }

    var filePath = Path.GetFullPath(o.Required("--file"));
    var version = ParseVersion(o.Required("--version"));
    var sigPath = filePath + ".sig";
    if (File.Exists(sigPath) && !o.Flag("--force"))
    {
        throw new UsageException($"{sigPath} already exists; pass --force to replace it");
    }

    var password = o.Flag("--password-stdin") ? ReadPasswordFromStdin() : PromptPassword("Key password: ");

    using var cert = X509CertificateLoader.LoadPkcs12FromFile(keyPath, password, X509KeyStorageFlags.EphemeralKeySet);
    using var privateKey = cert.GetECDsaPrivateKey() ?? throw new CryptographicException("The .pfx does not hold an ECDSA private key");
    if (privateKey.KeySize != 256)
    {
        throw new CryptographicException($"Expected a P-256 key, found {privateKey.KeySize}-bit");
    }

    var (size, sha256) = HashFile(filePath);
    var fileName = Path.GetFileName(filePath);
    var payload = BuildPayload(version, fileName, size, sha256);
    var signature = privateKey.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    var spki = privateKey.ExportSubjectPublicKeyInfo();
    using (var check = ECDsa.Create())
    {
        check.ImportSubjectPublicKeyInfo(spki, out _);
        if (!check.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            throw new CryptographicException("Self-check of the new signature failed");
        }
    }

    var sig = new SignatureFile(1, KeyIdOf(spki), version, fileName, size, sha256, Convert.ToBase64String(signature));
    File.WriteAllText(sigPath, JsonSerializer.Serialize(sig, SigJson.Options));

    Console.WriteLine($"Signed {fileName} v{version} ({size} bytes, sha256 {sha256})");
    Console.WriteLine($"Wrote {sigPath}");
    Console.WriteLine("Upload both files to the GitHub release.");
    return 0;
}

static int Verify(Options o)
{
    var filePath = Path.GetFullPath(o.Required("--file"));
    var version = ParseVersion(o.Required("--version"));
    var sigPath = o.Optional("--sig") ?? filePath + ".sig";

    var keyArg = o.Required("--public-key");
    var publicKey = File.Exists(keyArg)
        ? File.ReadAllLines(keyArg).Select(l => l.Trim()).First(l => l.StartsWith("publicKey=", StringComparison.Ordinal))["publicKey=".Length..]
        : keyArg;
    var spki = Convert.FromBase64String(publicKey);

    var sig = JsonSerializer.Deserialize<SignatureFile>(File.ReadAllText(sigPath), SigJson.Options)
        ?? throw new JsonException("Empty signature file");
    var (size, sha256) = HashFile(filePath);

    var failures = new List<string>();
    if (sig.Format != 1) failures.Add($"format {sig.Format}");
    if (sig.KeyId != KeyIdOf(spki)) failures.Add($"keyId {sig.KeyId} is not this public key");
    if (sig.Version != version) failures.Add($"version {sig.Version} != {version}");
    if (sig.File != Path.GetFileName(filePath)) failures.Add($"file {sig.File} != {Path.GetFileName(filePath)}");
    if (sig.Size != size) failures.Add($"size {sig.Size} != {size}");
    if (!string.Equals(sig.Sha256, sha256, StringComparison.Ordinal)) failures.Add("sha256 mismatch");

    using var key = ECDsa.Create();
    key.ImportSubjectPublicKeyInfo(spki, out _);
    var payload = BuildPayload(sig.Version, sig.File, sig.Size, sig.Sha256);
    if (!key.VerifyData(payload, Convert.FromBase64String(sig.Signature), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
    {
        failures.Add("signature invalid");
    }

    if (failures.Count > 0)
    {
        Console.Error.WriteLine("FAILED: " + string.Join("; ", failures));
        return 1;
    }

    Console.WriteLine($"OK: {sig.File} v{sig.Version} signed by {sig.KeyId}");
    return 0;
}

static byte[] BuildPayload(string version, string fileName, long size, string sha256) =>
    Encoding.UTF8.GetBytes($"ATDS-UPDATE-SIG-V1\nversion={version}\nfile={fileName}\nsize={size}\nsha256={sha256}");

static string KeyIdOf(byte[] spki) => Convert.ToHexStringLower(SHA256.HashData(spki))[..16];

static (long Size, string Sha256) HashFile(string path)
{
    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    return (fs.Length, Convert.ToHexStringLower(SHA256.HashData(fs)));
}

static string ParseVersion(string raw)
{
    var v = raw.TrimStart('v', 'V');
    if (!Version.TryParse(v, out var parsed) || parsed.Build < 0 || parsed.Revision >= 0)
    {
        throw new UsageException($"Version must be major.minor.patch, got '{raw}'");
    }

    return parsed.ToString(3);
}

static string PromptNewPassword()
{
    var first = PromptPassword($"New key password (min {MinPasswordLength} chars): ");
    if (first.Length < MinPasswordLength)
    {
        throw new UsageException($"Password must be at least {MinPasswordLength} characters");
    }

    if (PromptPassword("Confirm password: ") != first)
    {
        throw new UsageException("Passwords do not match");
    }

    return first;
}

static string PromptPassword(string prompt)
{
    if (Console.IsInputRedirected)
    {
        throw new UsageException("Input is redirected; use --password-stdin");
    }

    Console.Write(prompt);
    var sb = new StringBuilder();
    while (true)
    {
        var k = Console.ReadKey(intercept: true);
        if (k.Key == ConsoleKey.Enter) break;
        if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
        if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
    }

    Console.WriteLine();
    return sb.ToString();
}

static string ReadPasswordFromStdin()
{
    var line = Console.In.ReadLine();
    if (string.IsNullOrEmpty(line))
    {
        throw new UsageException("No password on stdin");
    }

    return line;
}

sealed record SignatureFile(int Format, string KeyId, string Version, string File, long Size, string Sha256, string Signature);

static class SigJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
}

sealed class UsageException(string message) : Exception(message);

sealed class Options
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                throw new UsageException($"Unexpected argument '{args[i]}'");
            }

            var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            o._values[args[i]] = hasValue ? args[++i] : null;
        }

        return o;
    }

    public string Required(string name) =>
        _values.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : throw new UsageException($"{name} is required");

    public string? Optional(string name) => _values.TryGetValue(name, out var v) ? v : null;

    public bool Flag(string name) => _values.ContainsKey(name);
}
