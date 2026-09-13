using System.Windows;
using DownloadManagerApplet.Converters;
using DownloadManagerApplet.Models;

namespace DownloadManagerApplet.Tests;

public class StatusToTextConverterTests
{
    private readonly StatusToTextConverter _converter = new();

    [Theory]
    [InlineData(DownloadStatus.Queued, "Queued")]
    [InlineData(DownloadStatus.Downloading, "Downloading")]
    [InlineData(DownloadStatus.Paused, "Paused · Resumable")]
    [InlineData(DownloadStatus.Completed, "Completed")]
    [InlineData(DownloadStatus.Error, "Error")]
    [InlineData(DownloadStatus.Canceled, "Canceled")]
    public void Convert_ReturnsExpectedText(DownloadStatus status, string expected)
    {
        var result = _converter.Convert(status, typeof(string), null, null!);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ConvertBack_ThrowsNotSupported()
    {
        Assert.Throws<NotSupportedException>(() => _converter.ConvertBack(null, typeof(DownloadStatus), null, null!));
    }
}

public class StatusToResumeLabelConverterTests
{
    private readonly StatusToResumeLabelConverter _converter = new();

    [Theory]
    [InlineData(DownloadStatus.Error, "Retry")]
    [InlineData(DownloadStatus.Canceled, "Retry")]
    [InlineData(DownloadStatus.Paused, "Resume")]
    [InlineData(DownloadStatus.Queued, "Resume")]
    public void Convert_ReturnsExpectedLabel(DownloadStatus status, string expected)
    {
        var result = _converter.Convert(status, typeof(string), null, null!);

        Assert.Equal(expected, result);
    }
}

public class StringToVisibilityConverterTests
{
    private readonly StringToVisibilityConverter _converter = new();

    [Theory]
    [InlineData(null, Visibility.Collapsed)]
    [InlineData("", Visibility.Collapsed)]
    [InlineData("something went wrong", Visibility.Visible)]
    public void Convert_ReturnsExpectedVisibility(string? value, Visibility expected)
    {
        var result = _converter.Convert(value, typeof(Visibility), null, null!);

        Assert.Equal(expected, result);
    }
}
