using System.Windows;
using DownloadManagerApplet.Converters;
using DownloadManagerApplet.Models;

namespace DownloadManagerApplet.Tests;

[Collection("WpfApplication")]
public class StatusToBrushConverterTests
{
    private readonly StatusToBrushConverter _converter = new();

    [Theory]
    [InlineData(DownloadStatus.Downloading, "AccentBrush")]
    [InlineData(DownloadStatus.Paused, "WarnBrush")]
    [InlineData(DownloadStatus.Completed, "OkBrush")]
    [InlineData(DownloadStatus.Error, "ErrBrush")]
    [InlineData(DownloadStatus.Canceled, "TextDimBrush")]
    [InlineData(DownloadStatus.Queued, "TextDimBrush")]
    public void Convert_ReturnsTheExpectedThemedBrush(DownloadStatus status, string expectedResourceKey)
    {
        var expected = Application.Current.TryFindResource(expectedResourceKey);

        var result = _converter.Convert(status, typeof(System.Windows.Media.Brush), null, null!);

        Assert.Same(expected, result);
    }
}

[Collection("WpfApplication")]
public class LogLevelToBrushConverterTests
{
    private readonly LogLevelToBrushConverter _converter = new();

    [Theory]
    [InlineData(LogLevelSetting.Debug, "TextDimBrush")]
    [InlineData(LogLevelSetting.Info, "TextDimBrush")]
    [InlineData(LogLevelSetting.Warn, "WarnBrush")]
    [InlineData(LogLevelSetting.Error, "ErrBrush")]
    [InlineData(LogLevelSetting.Critical, "ErrBrush")]
    [InlineData(LogLevelSetting.Fatal, "ErrBrush")]
    public void Convert_ReturnsTheExpectedThemedBrush(LogLevelSetting level, string expectedResourceKey)
    {
        var expected = Application.Current.TryFindResource(expectedResourceKey);

        var result = _converter.Convert(level, typeof(System.Windows.Media.Brush), null, null!);

        Assert.Same(expected, result);
    }
}
