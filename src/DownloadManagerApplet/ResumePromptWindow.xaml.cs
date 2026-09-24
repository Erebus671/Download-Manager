using System.Windows;
using System.Windows.Media;

namespace DownloadManagerApplet;

public partial class ResumePromptWindow : Window
{
    public ResumePromptWindow(ImageSource? titleIcon)
    {
        InitializeComponent();
        if (titleIcon is not null)
        {
            TitleIcon.Source = titleIcon;
            TitleIcon.Visibility = Visibility.Visible;
        }
    }

    private void Yes_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void No_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
