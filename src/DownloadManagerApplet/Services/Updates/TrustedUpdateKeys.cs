namespace DownloadManagerApplet.Services.Updates;

/// <summary>Public keys allowed to sign update installers (base64 SubjectPublicKeyInfo, P-256). Add a new key before retiring an old one.</summary>
public static class TrustedUpdateKeys
{
    public static readonly IReadOnlyList<string> All =
    [
        // AtraTech-Update-Signing.pfx, generated 2026-09-25.
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE5YmA/gqPRHgVxsfFlqFqA4HncEeIY4Iup9fqyX8+kQAy9xNGVMeTuQqJk9YoZJV9dBmUR92soTihHYVsjdFrOw=="
    ];
}
