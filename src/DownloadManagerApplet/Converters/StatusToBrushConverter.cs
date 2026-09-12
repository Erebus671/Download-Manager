using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using DownloadManagerApplet.Models;

namespace DownloadManagerApplet.Converters;

public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = value as DownloadStatus? ?? DownloadStatus.Queued;
        var key = status switch
        {
            DownloadStatus.Downloading => "AccentBrush",
            DownloadStatus.Paused => "WarnBrush",
            DownloadStatus.Completed => "OkBrush",
            DownloadStatus.Error => "ErrBrush",
            DownloadStatus.Canceled => "TextDimBrush",
            _ => "TextDimBrush"
        };

        return System.Windows.Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
