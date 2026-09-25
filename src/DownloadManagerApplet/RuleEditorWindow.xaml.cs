using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DownloadManagerApplet.ViewModels;

namespace DownloadManagerApplet;

public partial class RuleEditorWindow : Window
{
    private readonly RuleEditorViewModel _viewModel;

    public RuleEditorResult Result { get; private set; } = RuleEditorResult.Cancel;

    public RuleEditorWindow(RuleEditorViewModel viewModel, ImageSource? titleIcon)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        if (titleIcon is not null)
        {
            TitleIcon.Source = titleIcon;
            TitleIcon.Visibility = Visibility.Visible;
        }

        // Borderless dialog: let the user drag it by any empty area.
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.TextBox)
            {
                DragMove();
            }
        };
        Loaded += (_, _) => NameTextBox.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.TrySave())
        {
            Close(RuleEditorResult.Save);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close(RuleEditorResult.Cancel);

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this, $"Delete the rule \"{_viewModel.Name}\"?", "Delete rule", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes)
        {
            Close(RuleEditorResult.Delete);
        }
    }

    private void Close(RuleEditorResult result)
    {
        Result = result;
        DialogResult = result != RuleEditorResult.Cancel;
    }
}
