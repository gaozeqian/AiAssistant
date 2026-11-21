using System.Diagnostics;
using System.Runtime.InteropServices;
using AiAssistant.Desktop.ViewModels;
using AiAssistant.Desktop.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using CommunityToolkit.Mvvm.Input;

namespace AiAssistant.Desktop;

public sealed class ShellApp : Avalonia.Application, IDisposable
{
    private TrayIcon? _trayIcon;

    public static Func<ShellViewModel>? ViewModelFactory { get; set; }

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var shell = ViewModelFactory?.Invoke()
                ?? throw new InvalidOperationException("Desktop ViewModel factory was not configured by Host.");
            var icon = CreateApplicationIcon();
            var window = new MainWindow
            {
                DataContext = shell,
                Icon = icon
            };

            desktop.MainWindow = window;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // UI E2E hook: the app normally starts as a tray-only agent; this shows the window
            // immediately so an external AX driver can exercise the real window without a tray click.
            if (string.Equals(
                    Environment.GetEnvironmentVariable("AIASSISTANT_UI_E2E_SHOW"),
                    "1",
                    StringComparison.Ordinal))
            {
                window.Show();
            }

            ConfigureTray(desktop, window, shell, icon);
            shell.ExitRequested += (_, _) => desktop.Shutdown();
            desktop.ShutdownRequested += (_, _) =>
            {
                Dispose();
                shell.Dispose();
            };
            _ = InitializeShellSafelyAsync(shell);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task InitializeShellSafelyAsync(ShellViewModel shell)
    {
        try
        {
            await shell.InitializeAsync();
        }
        catch (Exception ex)
        {
            Trace.TraceError("Desktop shell initialization failed ({0}).", ex.GetType().Name);
        }
    }

    private void ConfigureTray(
        IClassicDesktopStyleApplicationLifetime desktop,
        MainWindow window,
        ShellViewModel shell,
        WindowIcon icon)
    {
        var menu = new NativeMenu();
        menu.Items.Add(new NativeMenuItem
        {
            Header = "打开时间线",
            Command = new RelayCommand(() =>
            {
                shell.ShowTimeline();
                window.ShowFromTray();
            })
        });
        menu.Items.Add(new NativeMenuItem
        {
            Header = "暂停 / 恢复采集",
            Command = shell.Capture.TogglePauseCommand
        });
        menu.Items.Add(new NativeMenuItem
        {
            Header = "立即快照",
            Command = shell.Capture.CaptureSnapshotCommand
        });
        menu.Items.Add(new NativeMenuItem
        {
            Header = "日报与站会",
            Command = new RelayCommand(() =>
            {
                shell.ShowReports();
                window.ShowFromTray();
            })
        });
        menu.Items.Add(new NativeMenuItem
        {
            Header = "本地诊断",
            Command = new RelayCommand(() =>
            {
                shell.ShowDiagnostics();
                window.ShowFromTray();
            })
        });
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(new NativeMenuItem
        {
            Header = "退出并清理",
            Command = new RelayCommand(() =>
            {
                shell.IsExitRequested = true;
                desktop.Shutdown();
            })
        });

        _trayIcon = new TrayIcon
        {
            Icon = icon,
            ToolTipText = "时光 · 本机活动追踪",
            Menu = menu,
            IsVisible = true,
            Command = new RelayCommand(window.ShowFromTray)
        };
        TrayIcon.SetIcons(this, [_trayIcon]);
    }

    public void Dispose()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
        GC.SuppressFinalize(this);
    }

    private static WindowIcon CreateApplicationIcon()
    {
        const int size = 32;
        using var bitmap = new WriteableBitmap(
            new PixelSize(size, size),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);
        using (var frame = bitmap.Lock())
        {
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var inside = x is >= 3 and < 29 && y is >= 3 and < 29;
                    var marker = x is >= 13 and < 19 || y is >= 13 and < 19;
                    var color = !inside ? 0u : marker ? 0xFFFFFFFFu : 0xFFD55731u;
                    Marshal.WriteInt32(frame.Address, y * frame.RowBytes + x * 4, unchecked((int)color));
                }
            }
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        stream.Position = 0;
        return new WindowIcon(stream);
    }
}
