namespace DownloadManagerApplet.Services.Updates;

/// <summary>Public keys allowed to sign update installers (base64 SubjectPublicKeyInfo, P-256). Add a new key before retiring an old one.</summary>
public static class TrustedUpdateKeys
{
    public static readonly IReadOnlyList<string> All =
    [
        // AtraTech-Update-Signing-v2.pfx, generated 2026-09-27 (keyId 2c4194b9a086b6dd). Replaced the 2026-09-25 key, whose password was lost.
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAErWoW9twkzeWUOb45hyTj678X4g37xGRPLohBlQ/RZe0E2mXpGvXNREnumWBGiDhkwplNMjncPlRVbMd/Cn2dmQ=="
    ];
}
