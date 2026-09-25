using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DownloadManagerApplet.ViewModels;

namespace DownloadManagerApplet;

public partial class FileTypesWindow : Window
{
    private readonly FileTypesEditorViewModel _viewModel;

    public FileTypesWindow(FileTypesEditorViewModel viewModel, ImageSource? titleIcon)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        if (titleIcon is not null)
        {
            TitleIcon.Source = titleIcon;
            TitleIcon.Visibility = Visibility.Visible;
        }

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.TextBox)
            {
                DragMove();
            }
        };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.TryValidate())
        {
            DialogResult = true;
        }
    }
}
