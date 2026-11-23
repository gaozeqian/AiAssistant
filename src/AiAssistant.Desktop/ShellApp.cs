using System.Diagnostics;
using AiAssistant.Core.Configuration;
using AiAssistant.Desktop.ViewModels;
using AiAssistant.Desktop.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using CommunityToolkit.Mvvm.Input;

namespace AiAssistant.Desktop;

public sealed class ShellApp : Avalonia.Application, IDisposable
{
    private TrayIcon? _trayIcon;
    private SingleInstance? _singleInstance;

    public static Func<ShellViewModel>? ViewModelFactory { get; set; }

    /// <summary>
    /// Set by Host from the platform capability matrix. Only used for diagnostics; closing the
    /// window always hides it and keeps capture running, quitting needs the explicit 退出 action.
    /// </summary>
    public static bool TrayAvailable { get; set; } = true;

    /// <summary>Unix socket used to bring the hidden window back when the app is launched again.</summary>
    public static string? InstanceSocketPath { get; set; }

    public ShellApp()
    {
        // Overrides Avalonia's default "Avalonia Application" so the macOS menu bar / Dock label read 「时光」.
        Name = "时光";
    }

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Resources.ThemeDictionaries.Add(ThemeVariant.Light, BuildThemeDictionary(isDark: false));
        Resources.ThemeDictionaries.Add(ThemeVariant.Dark, BuildThemeDictionary(isDark: true));
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

            // Wayland resolves the dock/top-bar icon by matching app_id to <app_id>.desktop;
            // X11 uses StartupWMClass=AiAssistant.Host from the same desktop entry.
            if (OperatingSystem.IsLinux())
            {
                WaylandProperties.SetAppId(window, "aiassistant");
            }

            // Closing hides the window; launching again (dock icon / desktop entry) shows it.
            if (!string.IsNullOrWhiteSpace(InstanceSocketPath))
            {
                _singleInstance = new SingleInstance(InstanceSocketPath);
                try
                {
                    _singleInstance.Start(() => Avalonia.Threading.Dispatcher.UIThread.Post(window.ShowFromTray));
                }
                catch
                {
                    // Activation is best-effort; capture keeps running regardless.
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
            if (showWindow || runUiE2E || !TrayAvailable)
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
            var initialization = InitializeShellSafelyAsync(shell);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Semantic brushes keyed by theme variant, referencing the VS Code Dark Modern / Light Modern
    /// palettes (neutral grays + #0078D4 accent). Controls reference them with {DynamicResource} so
    /// the whole window restyles when the theme toggles.
    /// </summary>
    private static ResourceDictionary BuildThemeDictionary(bool isDark)
    {
        var d = new ResourceDictionary();
        void Add(string key, string hex) => d[key] = new SolidColorBrush(Color.Parse(hex));

        if (!isDark)
        {
            // Light Modern
            Add("PageBackgroundBrush", "#F3F3F3");
            Add("CardBackgroundBrush", "#FFFFFF");
            Add("HeaderBackgroundBrush", "#EEEEEE");
            Add("HeaderBorderBrush", "#D4D4D4");
            Add("HeaderTextBrush", "#1F1F1F");
            Add("HeaderTextSecondaryBrush", "#616161");
            Add("HeaderBadgeBackgroundBrush", "#E5E5E5");
            Add("HeaderBadgeForegroundBrush", "#242424");
            Add("AccentBrush", "#0078D4");
            Add("AccentSoftBackgroundBrush", "#D6EBFF");
            Add("ListSelectionBackgroundBrush", "#CCE4FF");
            Add("TextPrimaryBrush", "#1F1F1F");
            Add("InputBackgroundBrush", "#FFFFFF");
            Add("InputBorderBrush", "#D4D4D4");
            Add("DatePickerFlyoutPresenterBackground", "#FFFFFF");
            Add("DatePickerFlyoutPresenterBorderBrush", "#C8C8C8");
            Add("MutedBrush", "#616161");
            Add("SubtleCardBrush", "#F3F3F3");
            Add("PlaceholderBrush", "#E5E5E5");
            Add("DividerBrush", "#D4D4D4");
            Add("SuccessForegroundBrush", "#22863A");
            Add("SuccessBackgroundBrush", "#E6F4EA");
            Add("DangerForegroundBrush", "#A1260D");
            Add("DangerBackgroundBrush", "#FBE9E7");
            Add("WarningForegroundBrush", "#895503");
            Add("WarningBackgroundBrush", "#FFF4CE");
        }
        else
        {
            // Dark Modern
            Add("PageBackgroundBrush", "#181818");
            Add("CardBackgroundBrush", "#1F1F1F");
            Add("HeaderBackgroundBrush", "#181818");
            Add("HeaderBorderBrush", "#2D2D2D");
            Add("HeaderTextBrush", "#D4D4D4");
            Add("HeaderTextSecondaryBrush", "#9D9D9D");
            Add("HeaderBadgeBackgroundBrush", "#2D2D2D");
            Add("HeaderBadgeForegroundBrush", "#E0E0E0");
            Add("AccentBrush", "#0078D4");
            Add("AccentSoftBackgroundBrush", "#123B63");
            Add("ListSelectionBackgroundBrush", "#04395E");
            Add("TextPrimaryBrush", "#D4D4D4");
            Add("InputBackgroundBrush", "#252526");
            Add("InputBorderBrush", "#3C3C3C");
            Add("DatePickerFlyoutPresenterBackground", "#252526");
            Add("DatePickerFlyoutPresenterBorderBrush", "#3F3F3F");
            Add("MutedBrush", "#A8A8A8");
            Add("SubtleCardBrush", "#252526");
            Add("PlaceholderBrush", "#2D2D2D");
            Add("DividerBrush", "#3C3C3C");
            Add("SuccessForegroundBrush", "#89D185");
            Add("SuccessBackgroundBrush", "#1E3B2A");
            Add("DangerForegroundBrush", "#F48771");
            Add("DangerBackgroundBrush", "#3B1E1E");
            Add("WarningForegroundBrush", "#CCA700");
            Add("WarningBackgroundBrush", "#3B3410");
        }

        return d;
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
