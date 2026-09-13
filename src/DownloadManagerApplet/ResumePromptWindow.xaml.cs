using System.Windows;

namespace DownloadManagerApplet;

public partial class ResumePromptWindow : Window
{
    public ResumePromptWindow()
    {
        InitializeComponent();
    }

    private void Yes_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void No_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
