using System.Globalization;
using System.Windows.Data;
using DownloadManagerApplet.Models;

namespace DownloadManagerApplet.Converters;

public sealed class StatusToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = value as DownloadStatus? ?? DownloadStatus.Queued;
        return status switch
        {
            DownloadStatus.Queued => "Queued",
            DownloadStatus.Downloading => "Downloading",
            DownloadStatus.Paused => "Paused · Resumable",
            DownloadStatus.Completed => "Completed",
            DownloadStatus.Error => "Error",
            DownloadStatus.Canceled => "Canceled",
            _ => status.ToString()
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
