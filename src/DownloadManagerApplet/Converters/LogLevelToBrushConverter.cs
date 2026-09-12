using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using DownloadManagerApplet.Models;

namespace DownloadManagerApplet.Converters;

public sealed class LogLevelToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var level = value as LogLevelSetting? ?? LogLevelSetting.Info;
        var key = level switch
        {
            LogLevelSetting.Warn => "WarnBrush",
            LogLevelSetting.Error or LogLevelSetting.Critical or LogLevelSetting.Fatal => "ErrBrush",
            _ => "TextDimBrush"
        };

        return System.Windows.Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
