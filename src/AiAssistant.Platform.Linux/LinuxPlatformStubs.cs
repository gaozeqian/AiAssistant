using AiAssistant.Application.Ports;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Platform.Linux;

/// <summary>
/// Linux capability matrix accurately reporting native Wayland default vs X11 fallback limitations (ADR-0006, IMPLEMENTATION.md §3.5).
/// </summary>
public sealed class LinuxCapabilityProvider(IKeyStore keyStore) : ICapabilityProvider
{
    public string PlatformName
    {
        get
        {
            var forced = Environment.GetEnvironmentVariable("AIASSISTANT_LINUX_BACKEND");
            if (string.Equals(forced, "X11", StringComparison.OrdinalIgnoreCase))
            {
                return "Linux (X11/XWayland forced compatibility)";
            }

            var hasWayland = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
            return hasWayland ? "Linux (Native Wayland default)" : "Linux (X11 fallback)";
        }
    }

    public IReadOnlyList<CapabilityState> GetCapabilities()
    {
        var hasWayland = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) &&
                         !string.Equals(Environment.GetEnvironmentVariable("AIASSISTANT_LINUX_BACKEND"), "X11", StringComparison.OrdinalIgnoreCase);

        return
        [
            hasWayland
                ? CapabilityState.PermissionRequired(CapabilityKind.ScreenCapture, "Wayland xdg-desktop-portal screen cast (S3)", "Authorize portal screen sharing prompt.")
                : CapabilityState.Degraded(CapabilityKind.ScreenCapture, "X11 XGetImage fallback (S3)"),
            hasWayland
                ? CapabilityState.Degraded(CapabilityKind.WindowActivity, "Wayland compositor restricts global active window inspection", "Falls back to portal metadata + AT-SPI where available.")
                : CapabilityState.Degraded(CapabilityKind.WindowActivity, "X11 EWMH active window (S3)"),
            CapabilityState.Supported(CapabilityKind.FileActivity, "Whitelisted directory inotify watcher ready"),
            hasWayland
                ? CapabilityState.Degraded(CapabilityKind.GlobalHotkey, "Wayland restricts global hotkeys; use tray or voice command")
                : CapabilityState.Degraded(CapabilityKind.GlobalHotkey, "X11 hotkey pending S3"),
            keyStore.Capability,
            CapabilityState.Degraded(CapabilityKind.SystemTray, "Requires StatusNotifierItem / AppIndicator on Linux desktops")
        ];
    }
}

public sealed class LinuxNoOpAutoStartService : IAutoStartService
{
    public CapabilityState Capability => CapabilityState.Degraded(CapabilityKind.AutoStart, "Pending S3 XDG autostart / systemd user unit");
    public Task<bool> IsEnabledAsync(CancellationToken ct) => Task.FromResult(false);
    public Task SetEnabledAsync(bool enabled, CancellationToken ct) => Task.CompletedTask;
}
