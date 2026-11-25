using System.Diagnostics;
using AiAssistant.Core.Configuration;
using AiAssistant.Desktop.ViewModels;
using AiAssistant.Desktop.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.Input;

namespace AiAssistant.Desktop;

public sealed class ShellApp : Avalonia.Application, IDisposable
{
    private readonly DesktopLaunchContext _context;
    private TrayIcon? _trayIcon;
    private SingleInstance? _singleInstance;

    public ShellApp(DesktopLaunchContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        // Overrides Avalonia's default "Avalonia Application" so the macOS menu bar / Dock label read 「时光」.
        Name = "时光";
    }

    public override void Initialize() => ShellTheme.Apply(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var shell = _context.ViewModelFactory();
            var icon = CreateApplicationIcon();
            var window = new MainWindow
            {
                DataContext = shell,
                Icon = icon
            };

            desktop.MainWindow = window;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Wire the save-file dialog into export-capable view models once a TopLevel exists.
            var storage = TopLevel.GetTopLevel(window)?.StorageProvider;
            if (storage is not null)
            {
                var picker = new AvaloniaSaveFilePicker(storage);
                shell.Reports.SaveFilePicker = picker;
                if (shell.Search is not null)
                {
                    shell.Search.SaveFilePicker = picker;
                }

                shell.Diagnostics.SaveFilePicker = picker;
            }

            // Wayland resolves the dock/top-bar icon by matching app_id to <app_id>.desktop;
            // X11 uses StartupWMClass=AiAssistant.Host from the same desktop entry.
            if (OperatingSystem.IsLinux())
            {
                WaylandProperties.SetAppId(window, "aiassistant");
            }

            // Closing hides the window; launching again (dock icon / desktop entry) shows it.
            if (!string.IsNullOrWhiteSpace(_context.InstanceSocketPath))
            {
                _singleInstance = new SingleInstance(_context.InstanceSocketPath);
                try
                {
                    _singleInstance.Start(() => Avalonia.Threading.Dispatcher.UIThread.Post(window.ShowFromTray));
                }
                catch (Exception ex)
                {
                    // Activation is best-effort; capture keeps running regardless.
                    AiAssistant.Application.Diagnostics.SuppressedExceptionLog.Trace("Desktop.ShellApp.OnFrameworkInitializationCompleted", ex);
                }
            }

            // Follow the system theme on first launch, then honour manual toggles.
            // AIASSISTANT_THEME=light|dark pins a variant (useful for tests and user preference).
            var themeOverride = Environment.GetEnvironmentVariable(EnvironmentNames.Theme);
            shell.ThemeVariant = themeOverride switch
            {
                "light" => ThemeVariant.Light,
                "dark" => ThemeVariant.Dark,
                _ => ActualThemeVariant == ThemeVariant.Dark ? ThemeVariant.Dark : ThemeVariant.Light
            };
            RequestedThemeVariant = shell.ThemeVariant;
            shell.ThemeVariantChanged += variant => RequestedThemeVariant = variant;

            // UI E2E hook: the app normally starts as a tray-only agent; this shows the window
            // immediately so an external driver (or the in-app runner below) can exercise the real window.
            var showWindow = string.Equals(
                Environment.GetEnvironmentVariable(EnvironmentNames.UiE2EShow),
                "1",
                StringComparison.Ordinal);
            var runUiE2E = string.Equals(
                Environment.GetEnvironmentVariable(EnvironmentNames.UiE2E),
                "1",
                StringComparison.Ordinal);
            // Tray-only on desktops that actually have a tray; without one the window must be
            // visible, otherwise the app would start with no reachable UI at all.
            if (showWindow || runUiE2E || !_context.TrayAvailable)
            {
                window.Show();
            }

            ConfigureTray(desktop, window, shell, icon);
            shell.ExitRequested += (_, _) => desktop.Shutdown();
            shell.RestartRequested += () => Restart(desktop);
            desktop.ShutdownRequested += (_, _) =>
            {
                Dispose();
                shell.Dispose();
            };
            var initialization = InitializeShellSafelyAsync(shell);

            if (runUiE2E && _context.UiE2ERunner is { } runner)
            {
                var outputRoot = Environment.GetEnvironmentVariable(EnvironmentNames.UiE2EOutput)
                    ?? AiAssistant.Core.Configuration.ArtifactPaths.Resolve("test-results", "e2e");
                Directory.CreateDirectory(outputRoot);
                _ = RunUiE2EAsync(runner, window, shell, initialization, desktop, outputRoot);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task RunUiE2EAsync(
        Func<MainWindow, ShellViewModel, Task<Exception?>, Action<int>, string, Task<int>> runner,
        MainWindow window,
        ShellViewModel shell,
        Task<Exception?> initialization,
        IClassicDesktopStyleApplicationLifetime desktop,
        string outputRoot)
    {
        try
        {
            var exitCode = await runner(window, shell, initialization, desktop.Shutdown, outputRoot);
            desktop.Shutdown(exitCode);
        }
        catch (Exception ex)
        {
            Trace.TraceError("UI E2E runner failed ({0}).", ex.GetType().Name);
            desktop.Shutdown(2);
        }
    }

    /// <summary>Relaunches the process and shuts the current instance down so model config can apply.</summary>
    private void Restart(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // Release the single-instance socket before relaunching so the new process starts fresh.
        _singleInstance?.Dispose();
        _singleInstance = null;

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            desktop.Shutdown();
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // Restart failed; still shut down so the user can relaunch manually.
            AiAssistant.Application.Diagnostics.SuppressedExceptionLog.Trace("Desktop.ShellApp.Restart", ex);
        }

        desktop.Shutdown();
    }

    private static async Task<Exception?> InitializeShellSafelyAsync(ShellViewModel shell)
    {
        try
        {
            await shell.InitializeAsync();
            return null;
        }
        catch (Exception ex)
        {
            Trace.TraceError("Desktop shell initialization failed ({0}).", ex.GetType().Name);
            return ex;
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
        _singleInstance?.Dispose();
        _singleInstance = null;
        GC.SuppressFinalize(this);
    }

    private static WindowIcon CreateApplicationIcon()
    {
        using var stream = typeof(ShellApp).Assembly.GetManifestResourceStream(
            "AiAssistant.Desktop.Assets.aiassistant.png")
            ?? throw new InvalidOperationException("Embedded app icon 'AiAssistant.Desktop.Assets.aiassistant.png' is missing.");
        return new WindowIcon(new Bitmap(stream));
    }
}
