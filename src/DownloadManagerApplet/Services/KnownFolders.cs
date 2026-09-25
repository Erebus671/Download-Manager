using System.IO;
using System.Runtime.InteropServices;
using DownloadManagerApplet.Models;

namespace DownloadManagerApplet.Services;

/// <summary>Windows known folders via SHGetKnownFolderPath, so redirected folders (e.g. OneDrive) are honored.</summary>
public static class KnownFolders
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");
    private static readonly Guid VideosId = new("18989B1D-99B5-455B-841C-AB7C74E4DDFC");
    private static readonly Guid PicturesId = new("33E28130-4E1E-4676-835A-98395C3BC3BB");
    private static readonly Guid MusicId = new("4BD8D571-6D19-48D3-BE97-422220080E43");
    private static readonly Guid DocumentsId = new("FDD39AD0-238F-46AF-ADB4-6C85480369C7");

    public static string Downloads => Get(DownloadsId, () => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));

    public static string For(FileCategory category) => category switch
    {
        FileCategory.Videos => Get(VideosId, () => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)),
        FileCategory.Images => Get(PicturesId, () => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
        FileCategory.Audio => Get(MusicId, () => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)),
        FileCategory.Documents => Get(DocumentsId, () => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
        _ => Downloads
    };

    private static string Get(Guid id, Func<string> fallback)
    {
        try
        {
            var path = SHGetKnownFolderPath(id, 0, IntPtr.Zero);
            if (!string.IsNullOrWhiteSpace(path))
            {
                return path;
            }
        }
        catch (Exception ex) when (ex is COMException or ExternalException or EntryPointNotFoundException or DllNotFoundException)
        {
            // Fall through to the SpecialFolder equivalent.
        }

        var fallbackPath = fallback();
        return string.IsNullOrWhiteSpace(fallbackPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
            : fallbackPath;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = false)]
    private static extern string SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken);
}
