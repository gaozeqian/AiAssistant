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
/// A supported capability that still carries a hint (for example voice models ready but the
/// session not enabled) keeps its follow-up, otherwise the only useful step stays hidden.
/// </summary>
internal static class CapabilityActionResolver
{
    public static CapabilityAction? Resolve(CapabilityState capability) => capability.Status switch
    {
        CapabilityStatus.PermissionRequired => Permission(capability.Kind) ?? Guidance(capability),
        CapabilityStatus.Degraded or CapabilityStatus.Unavailable or CapabilityStatus.Supported =>
            Guidance(capability),
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

    /// <summary>
    /// Maps the guidance promised by a hint to the follow-up that can actually carry it out.
    /// OCR assets are resolved outside the app, so the only useful step is a fresh probe.
    /// </summary>
    private static CapabilityAction? Guidance(CapabilityState capability) =>
        string.IsNullOrWhiteSpace(capability.RemediationHint)
            ? null
            : capability.Kind switch
            {
                CapabilityKind.WakeWord => new("前往语音", CapabilityActionKind.OpenVoicePage),
                CapabilityKind.AudioCapture => new("前往语音", CapabilityActionKind.OpenVoicePage),
                CapabilityKind.Ocr => new("重新检查", CapabilityActionKind.RetryDiagnostics),
                _ => null
            };
}
