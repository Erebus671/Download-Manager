using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DownloadManagerApplet.Services;
using DownloadManagerApplet.Services.Updates;
using DownloadManagerApplet.ViewModels;

namespace DownloadManagerApplet;

public partial class UpdatePromptWindow : Window
{
    public UpdatePromptResult Result { get; private set; } = UpdatePromptResult.Later;

    public UpdatePromptWindow(VerifiedUpdate update, string currentVersion, int activeDownloads, ImageSource? titleIcon, ILoggingService log)
    {
        InitializeComponent();
        if (titleIcon is not null)
        {
            TitleIcon.Source = titleIcon;
            TitleIcon.Visibility = Visibility.Visible;
        }

        HeadingText.Text = $"Update ready: version {update.Manifest.Version.ToString(3)}";
        DetailRun.Text = $"You have {currentVersion} · {FormatSize(update.Manifest.Size)}";
        ShowNotes(update.Release.Notes, log);

        if (activeDownloads > 0)
        {
            ActivePanel.Visibility = Visibility.Visible;
            ActiveText.Text = activeDownloads == 1 ? "1 download active" : $"{activeDownloads} downloads active";
            ActiveNoteText.Text = activeDownloads == 1 ? "It will pause and resume after the update." : "They will pause and resume after the update.";
        }
    }

    private void ShowNotes(string? notes, ILoggingService log)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            NotesPanel.Children.Add(PlainNotes("No release notes."));
            return;
        }

        try
        {
            var blocks = ReleaseNotesMarkdown.Parse(notes);
            foreach (var element in ReleaseNotesRenderer.Render(blocks))
            {
                NotesPanel.Children.Add(element);
            }
            if (blocks.Count > 0 && blocks[0].Kind == NotesBlockKind.Heading)
            {
                NotesLabel.Visibility = Visibility.Collapsed;
            }
            log.Debug($"Update prompt: rendered {blocks.Count} release-note blocks from {notes.Length} chars");
        }
        catch (Exception ex)
        {
            log.Error("Update prompt: release notes failed to render; showing plain text", ex);
            NotesPanel.Children.Clear();
            NotesLabel.Visibility = Visibility.Visible;
            NotesPanel.Children.Add(PlainNotes(notes));
        }
    }

    private static TextBlock PlainNotes(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        return block;
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
