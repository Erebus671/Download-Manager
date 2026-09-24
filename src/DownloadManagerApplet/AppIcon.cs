using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DownloadManagerApplet.Services;

namespace DownloadManagerApplet;

internal static class AppIcon
{
    // Reads the icon embedded at publish time (-p:AppIconPath) so the artwork never lives in the repo.
    public static ImageSource? TryLoadSmall(ILoggingService log)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            log.Warn("Title bar icon: process path unavailable; showing no icon.");
            return null;
        }

        var small = new IntPtr[1];
        try
        {
            var extracted = NativeMethods.ExtractIconEx(exePath, 0, null, small, 1);
            if (extracted == 0 || small[0] == IntPtr.Zero)
            {
                log.Debug($"Title bar icon: no embedded icon in '{exePath}'; showing no icon.");
                return null;
            }

            var source = Imaging.CreateBitmapSourceFromHIcon(small[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            log.Debug($"Title bar icon: loaded {source.PixelWidth}x{source.PixelHeight} icon from '{exePath}'.");
            return source;
        }
        catch (Exception ex)
        {
            log.Warn($"Title bar icon: extraction failed ({ex.GetType().Name}: {ex.Message}); showing no icon.");
            return null;
        }
        finally
        {
            if (small[0] != IntPtr.Zero && !NativeMethods.DestroyIcon(small[0]))
            {
                log.Warn($"Title bar icon: DestroyIcon failed (Win32 error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}).");
            }
        }
    }
}
