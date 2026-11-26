using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Desktop.ViewModels;

public enum CapabilityActionKind
{
    OpenPermissionSettings,
    OpenVoicePage,
    RetryDiagnostics
}

/// <summary>A concrete, user-facing follow-up derived from a capability state.</summary>
public sealed record CapabilityAction(
    string Label,
    CapabilityActionKind Kind,
    string? PermissionTarget = null);

/// <summary>
/// Maps a capability to the action promised by its remediation hint. Informational degradations
/// deliberately get no button: an action that cannot fix the reported state is worse than none.
/// </summary>
internal static class CapabilityActionResolver
{
    public static CapabilityAction? Resolve(CapabilityState capability) => capability.Status switch
    {
        CapabilityStatus.PermissionRequired => Permission(capability.Kind),
        CapabilityStatus.Degraded or CapabilityStatus.Unavailable => Guidance(capability),
        _ => null
    };

    public static bool IsAvailable(CapabilityAction action) =>
        action.Kind != CapabilityActionKind.OpenPermissionSettings ||
        SettingsUri(action.PermissionTarget) is not null;

    public static string? SettingsUri(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return null;
        }

        if (OperatingSystem.IsWindows())
        {
            return target switch
            {
                "microphone" => "ms-settings:privacy-microphone",
                "startup" => "ms-settings:startupapps",
                _ => "ms-settings:privacy"
            };
        }

        if (OperatingSystem.IsMacOS())
        {
            return target switch
            {
                "screen-recording" => "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture",
                "accessibility" => "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility",
                "microphone" => "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone",
                "startup" => "x-apple.systempreferences:com.apple.preference.general",
                _ => "x-apple.systempreferences:com.apple.preference.security"
            };
        }

        // Linux has no cross-desktop deep link; showing a dead button would be misleading.
        return null;
    }

    private static CapabilityAction? Permission(CapabilityKind kind) => kind switch
    {
        CapabilityKind.KeyStore => new("重新检查", CapabilityActionKind.RetryDiagnostics),
        CapabilityKind.ScreenCapture => new("去授权", CapabilityActionKind.OpenPermissionSettings, "screen-recording"),
        CapabilityKind.WindowActivity or CapabilityKind.AccessibilityText =>
            new("去授权", CapabilityActionKind.OpenPermissionSettings, "accessibility"),
        CapabilityKind.AudioCapture or CapabilityKind.WakeWord =>
            new("去授权", CapabilityActionKind.OpenPermissionSettings, "microphone"),
        CapabilityKind.AutoStart => new("去授权", CapabilityActionKind.OpenPermissionSettings, "startup"),
        _ => null
    };

    private static CapabilityAction? Guidance(CapabilityState capability) =>
        capability.Kind == CapabilityKind.WakeWord &&
        !string.IsNullOrWhiteSpace(capability.RemediationHint)
            ? new("前往语音", CapabilityActionKind.OpenVoicePage)
            : null;
}
