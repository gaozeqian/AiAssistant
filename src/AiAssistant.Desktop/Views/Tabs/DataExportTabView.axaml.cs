using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace AiAssistant.Desktop.Views.Tabs;

public sealed partial class DataExportTabView : TabViewBase
{
    public DataExportTabView()
    {
        InitializeComponent();
    }

    private async void OnCopyBackupKeyClick(object? sender, RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null || string.IsNullOrEmpty(BackupKeyBox.Text))
        {
            return;
        }

        await clipboard.SetTextAsync(BackupKeyBox.Text);
    }
}
