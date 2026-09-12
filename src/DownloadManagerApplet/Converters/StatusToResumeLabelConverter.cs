using System.Globalization;
using System.Windows.Data;
using DownloadManagerApplet.Models;

namespace DownloadManagerApplet.Converters;

public sealed class StatusToResumeLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = value as DownloadStatus? ?? DownloadStatus.Queued;
        return status == DownloadStatus.Error ? "Retry" : "Resume";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
