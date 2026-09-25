using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using DownloadManagerApplet.Services.Updates;
using DownloadManagerApplet.ViewModels;

namespace DownloadManagerApplet;

public partial class MainWindow : Window
{
    private readonly ImageSource? _titleBarIcon;
    private bool _updatePromptOpen;

    public MainWindow(ImageSource? titleBarIcon)
    {
        InitializeComponent();
        _titleBarIcon = titleBarIcon;
        if (titleBarIcon is not null)
        {
            TitleBarIcon.Source = titleBarIcon;
            TitleBarIcon.Visibility = Visibility.Visible;
        }
        StateChanged += (_, _) => UpdateMaximizeRestoreIcon();
        Loaded += MainWindow_Loaded;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // WindowChrome + WindowStyle=None windows maximize to the full monitor bounds
        // (overlapping the taskbar and, on a multi-monitor layout, adjacent screens) unless
        // WM_GETMINMAXINFO is handled to clamp to the monitor's work area.
        if (PresentationSource.FromVisual(this) is HwndSource hwndSource)
        {
            hwndSource.AddHook(WindowProc);
        }
    }

    private static IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg == WM_GETMINMAXINFO)
        {
            ClampMaxSizeToWorkArea(hwnd, lParam);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private static void ClampMaxSizeToWorkArea(IntPtr hwnd, IntPtr lParam)
    {
        var mmi = Marshal.PtrToStructure<NativeMethods.MINMAXINFO>(lParam);

        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero)
        {
            var monitorInfo = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            NativeMethods.GetMonitorInfo(monitor, ref monitorInfo);

            var workArea = monitorInfo.rcWork;
            var monitorArea = monitorInfo.rcMonitor;

            mmi.ptMaxPosition.X = workArea.Left - monitorArea.Left;
            mmi.ptMaxPosition.Y = workArea.Top - monitorArea.Top;
            mmi.ptMaxSize.X = workArea.Right - workArea.Left;
            mmi.ptMaxSize.Y = workArea.Bottom - workArea.Top;
        }

        Marshal.StructureToPtr(mmi, lParam, true);
    }

    /// <summary>Restore and focus; relies on the caller having granted AllowSetForegroundWindow.</summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
        {
            SystemCommands.RestoreWindow(this);
        }

        Show();
        Activate();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void MaximizeRestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateMaximizeRestoreIcon()
    {
        var maximized = WindowState == WindowState.Maximized;
        MaximizeIcon.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        RestoreIcon.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        viewModel.ShowRenameDialog = editor =>
            new RenameWindow(editor, _titleBarIcon) { Owner = this }.ShowDialog() == true;

        var destinations = viewModel.Destinations;
        destinations.ShowRuleEditor = editor =>
        {
            var dialog = new RuleEditorWindow(editor, _titleBarIcon) { Owner = this };
            dialog.ShowDialog();
            return dialog.Result;
        };
        destinations.ShowFileTypesEditor = editor =>
            new FileTypesWindow(editor, _titleBarIcon) { Owner = this }.ShowDialog() == true;
        destinations.Confirm = question =>
            MessageBox.Show(this, question, "AtraTech Download Solutions", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

        if (viewModel.Updates is { } updates)
        {
            updates.PromptRequested += update => ShowUpdatePrompt(viewModel, updates, update);
        }

        if (viewModel.HasDownloadsToResumeAfterUpdate)
        {
            viewModel.ResumeAfterUpdate();
        }
        else if (viewModel.HasRestoredPendingDownloads)
        {
            var prompt = new ResumePromptWindow(_titleBarIcon) { Owner = this };
            if (prompt.ShowDialog() == true)
            {
                viewModel.ResumeAllCommand.Execute(null);
            }
        }
    }

    private void ShowUpdatePrompt(MainViewModel viewModel, UpdatesViewModel updates, VerifiedUpdate update)
    {
        if (_updatePromptOpen)
        {
            return;
        }

        _updatePromptOpen = true;
        try
        {
            var prompt = new UpdatePromptWindow(update, updates.CurrentVersion, viewModel.ActiveDownloadCount, _titleBarIcon) { Owner = this };
            prompt.ShowDialog();
            updates.OnPromptResult(update, prompt.Result);
        }
        finally
        {
            _updatePromptOpen = false;
        }
    }
}
