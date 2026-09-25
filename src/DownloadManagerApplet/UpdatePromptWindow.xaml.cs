using System.Windows;
using System.Windows.Media;
using DownloadManagerApplet.Services.Updates;
using DownloadManagerApplet.ViewModels;

namespace DownloadManagerApplet;

public partial class UpdatePromptWindow : Window
{
    public UpdatePromptResult Result { get; private set; } = UpdatePromptResult.Later;

    public UpdatePromptWindow(VerifiedUpdate update, string currentVersion, int activeDownloads, ImageSource? titleIcon)
    {
        InitializeComponent();
        if (titleIcon is not null)
        {
            TitleIcon.Source = titleIcon;
            TitleIcon.Visibility = Visibility.Visible;
        }

        HeadingText.Text = $"Update ready: version {update.Manifest.Version.ToString(3)}";
        DetailRun.Text = $"You have {currentVersion} · {FormatSize(update.Manifest.Size)}";
        NotesText.Text = string.IsNullOrWhiteSpace(update.Release.Notes) ? "No release notes." : update.Release.Notes;

        if (activeDownloads > 0)
        {
            ActivePanel.Visibility = Visibility.Visible;
            ActiveText.Text = activeDownloads == 1 ? "1 download active" : $"{activeDownloads} downloads active";
            ActiveNoteText.Text = activeDownloads == 1 ? "It will pause and resume after the update." : "They will pause and resume after the update.";
        }
    }

    private static string FormatSize(long bytes) => $"{bytes / 1024d / 1024d:0.0} MB";

    private void Install_Click(object sender, RoutedEventArgs e) => Close(UpdatePromptResult.Install);

    private void Later_Click(object sender, RoutedEventArgs e) => Close(UpdatePromptResult.Later);

    private void Skip_Click(object sender, RoutedEventArgs e) => Close(UpdatePromptResult.Skip);

    private void Close(UpdatePromptResult result)
    {
        Result = result;
        DialogResult = true;
    }
}
