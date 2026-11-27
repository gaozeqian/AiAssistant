using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace AiAssistant.Desktop.Views.Tabs;

public sealed partial class DataExportTabView : TabViewBase
{
    public DataExportTabView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// NumericUpDown 把键盘焦点交给模板内的 TextBox，而 AutomationProperties.Name 挂在容器上，
    /// 读屏会念成"编辑框"。模板应用后把名字同步到内部 TextBox。
    /// </summary>
    private void OnRetentionSpinnerLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not NumericUpDown spinner)
        {
            return;
        }

        var name = AutomationProperties.GetName(spinner);
        var editor = spinner.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
        if (editor is not null && !string.IsNullOrWhiteSpace(name))
        {
            AutomationProperties.SetName(editor, name);
        }
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
