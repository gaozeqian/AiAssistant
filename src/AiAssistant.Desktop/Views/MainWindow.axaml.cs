using AiAssistant.Desktop.ViewModels;
using Avalonia.Controls;

namespace AiAssistant.Desktop.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is ShellViewModel { IsExitRequested: false })
        {
            // Closing the window only hides it: capture keeps running in the background.
            // Quitting requires the explicit 「退出」 button (or the tray menu).
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }
}
