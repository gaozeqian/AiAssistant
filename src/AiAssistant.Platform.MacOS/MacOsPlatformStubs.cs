using AiAssistant.Application.Ports;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Platform.MacOS;

/// <summary>
/// Capability matrix &amp; safe stubs for macOS arm64 until S3 Swift Helper IPC adapters are connected (ADR-0005).
/// </summary>
public sealed class MacOsCapabilityProvider(IKeyStore keyStore) : ICapabilityProvider
{
    public string PlatformName => "macOS (Apple Silicon)";

    public IReadOnlyList<CapabilityState> GetCapabilities() =>
    [
        CapabilityState.PermissionRequired(CapabilityKind.ScreenCapture, "Pending S3 Swift Helper ScreenCaptureKit", "Grant Screen Recording in System Settings > Privacy & Security."),
        CapabilityState.PermissionRequired(CapabilityKind.WindowActivity, "Pending S3 Swift Helper NSWorkspace + AX", "Grant Accessibility permission."),
        CapabilityState.Supported(CapabilityKind.FileActivity, "Whitelisted directory FileSystemWatcher ready"),
        keyStore.Capability,
        CapabilityState.Supported(CapabilityKind.SystemTray, "Avalonia TrayIcon ready"),
        CapabilityState.Degraded(CapabilityKind.AutoStart, "Pending S3 LaunchAgent adapter")
    ];
}

public sealed class MacOsNoOpAutoStartService : IAutoStartService
{
    public CapabilityState Capability => CapabilityState.Degraded(CapabilityKind.AutoStart, "Pending S3 LaunchAgent adapter");
    public Task<bool> IsEnabledAsync(CancellationToken ct) => Task.FromResult(false);
    public Task SetEnabledAsync(bool enabled, CancellationToken ct) => Task.CompletedTask;
}
