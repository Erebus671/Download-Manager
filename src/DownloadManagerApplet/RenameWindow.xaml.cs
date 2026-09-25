using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DownloadManagerApplet.ViewModels;

namespace DownloadManagerApplet;

public partial class RenameWindow : Window
{
    private readonly RenameViewModel _viewModel;

    public RenameWindow(RenameViewModel viewModel, ImageSource? titleIcon)
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
        Loaded += (_, _) =>
        {
            // Select the name without its extension, like Explorer.
            NameTextBox.Focus();
            NameTextBox.Select(0, Path.GetFileNameWithoutExtension(viewModel.Name).Length);
        };
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.TrySave())
        {
            DialogResult = true;
        }
    }
}
