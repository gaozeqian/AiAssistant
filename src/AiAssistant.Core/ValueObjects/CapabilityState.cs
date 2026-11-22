namespace AiAssistant.Core.ValueObjects;

public enum CapabilityStatus
{
    Supported = 0,
    PermissionRequired = 1,
    Degraded = 2,
    Unavailable = 3,
    DisabledByPolicy = 4
}

public enum CapabilityKind
{
    ScreenCapture = 0,
    WindowActivity = 1,
    FileActivity = 2,
    AudioCapture = 3,
    AccessibilityText = 4,
    Ocr = 5,
    GlobalHotkey = 6,
    SystemTray = 7,
    AutoStart = 8,
    KeyStore = 9,
    VectorSearch = 10,
    WakeWord = 11,
    ModelGateway = 12
}

/// <summary>
/// Uniform platform capability model driving UI &amp; scheduling without scattering OS checks (implementation.md §3.5).
/// </summary>
public sealed record CapabilityState(
    CapabilityKind Kind,
    CapabilityStatus Status,
    string Summary,
    string? RemediationHint = null)
{
    public bool IsOperational => Status is CapabilityStatus.Supported or CapabilityStatus.Degraded;

    public static CapabilityState Supported(CapabilityKind kind, string summary) =>
        new(kind, CapabilityStatus.Supported, summary);

    public static CapabilityState Degraded(CapabilityKind kind, string summary, string? hint = null) =>
        new(kind, CapabilityStatus.Degraded, summary, hint);

    public static CapabilityState PermissionRequired(CapabilityKind kind, string summary, string? hint = null) =>
        new(kind, CapabilityStatus.PermissionRequired, summary, hint);

    public static CapabilityState Unavailable(CapabilityKind kind, string summary, string? hint = null) =>
        new(kind, CapabilityStatus.Unavailable, summary, hint);

    public static CapabilityState DisabledByPolicy(CapabilityKind kind, string summary) =>
        new(kind, CapabilityStatus.DisabledByPolicy, summary);
}
