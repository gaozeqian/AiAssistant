using AiAssistant.Desktop.ViewModels;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace AiAssistant.Desktop.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnDatePickerInitialized(object? sender, System.EventArgs e)
    {
        if (sender is not DatePicker picker)
        {
            return;
        }

        picker.Loaded += (_, _) =>
        {
            // Defer until the template has been applied and laid out.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var popups = picker.GetVisualDescendants().OfType<Popup>().ToArray();
                foreach (var popup in popups)
                {
                    // Keep the wheel below its DatePicker instead of flipping over the tab strip.
                    popup.Placement = PlacementMode.Bottom;
                    popup.Opened -= OnPickerPopupOpened;
                    popup.Opened += OnPickerPopupOpened;
                }
            }, Avalonia.Threading.DispatcherPriority.Background);
        };
    }

    // DatePickerPresenter lives in the Popup's own visual tree, so Window.Styles cannot reach it.
    // On open, restyle the wheel: text OK/Cancel buttons and scroll padding to avoid clipped edges.
    private void OnPickerPopupOpened(object? sender, System.EventArgs e)
    {
        if (sender is not Popup popup)
        {
            return;
        }

        var presenter = popup.Child?.GetVisualDescendants().OfType<DatePickerPresenter>().FirstOrDefault();
        if (presenter is null)
        {
            presenter = popup.Child as DatePickerPresenter;
        }
        if (presenter is null)
        {
            return;
        }

        SetButtonText(presenter, "PART_AcceptButton", "确定");
        SetButtonText(presenter, "PART_DismissButton", "取消");
        // Soft top/bottom fade so edge items don't look abruptly clipped.
        foreach (var scroll in presenter.GetVisualDescendants().OfType<ScrollViewer>())
        {
            scroll.Padding = new Avalonia.Thickness(0, 24);
        }
    }

    private static void SetButtonText(DatePickerPresenter presenter, string partName, string text)
    {
        var buttons = presenter.GetVisualDescendants().OfType<Button>().ToArray();
        var button = buttons.FirstOrDefault(b => b.Name == partName);
        if (button is not null)
        {
            button.Content = new TextBlock
            {
                Text = text,
                FontSize = 13,
                Foreground = button.Foreground,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
        }
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
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }
}
