namespace AiAssistant.Platform.Linux.Desktop;

public enum LinuxDisplayBackendKind
{
    Wayland = 0,
    X11 = 1,
    Unavailable = 2
}

public enum LinuxBackendSelectionReason
{
    DefaultWayland = 0,
    ForcedByUser = 1,
    MissingWaylandDisplay = 2,
    InitializationFailure = 3,
    NoDisplay = 4
}

public sealed record LinuxDesktopSessionSnapshot(
    bool IsLinux,
    bool HasWaylandDisplay,
    bool HasX11Display,
    string? ForcedBackend,
    string? Desktop,
    string? SessionType)
{
    public static LinuxDesktopSessionSnapshot Detect(Func<string, string?>? environment = null)
    {
        var env = environment ?? Environment.GetEnvironmentVariable;
        return new LinuxDesktopSessionSnapshot(
            OperatingSystem.IsLinux(),
            !string.IsNullOrWhiteSpace(env("WAYLAND_DISPLAY")),
            !string.IsNullOrWhiteSpace(env("DISPLAY")),
            env("AIASSISTANT_LINUX_BACKEND"),
            env("XDG_CURRENT_DESKTOP"),
            env("XDG_SESSION_TYPE"));
    }
}

public sealed record LinuxDisplayBackendDecision(
    LinuxDisplayBackendKind Backend,
    LinuxBackendSelectionReason Reason,
    string Summary,
    bool CanFallbackToX11);

public static class LinuxDisplayBackendSelector
{
    public static LinuxDisplayBackendDecision Select(LinuxDesktopSessionSnapshot session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var forced = session.ForcedBackend?.Trim();
        if (string.Equals(forced, "X11", StringComparison.OrdinalIgnoreCase))
        {
            return session.HasX11Display
                ? new LinuxDisplayBackendDecision(LinuxDisplayBackendKind.X11, LinuxBackendSelectionReason.ForcedByUser, "X11/XWayland explicitly selected by user", false)
                : new LinuxDisplayBackendDecision(LinuxDisplayBackendKind.Unavailable, LinuxBackendSelectionReason.NoDisplay, "X11 was requested but DISPLAY is unavailable", false);
        }

        if (string.Equals(forced, "Wayland", StringComparison.OrdinalIgnoreCase))
        {
            return session.HasWaylandDisplay
                ? new LinuxDisplayBackendDecision(LinuxDisplayBackendKind.Wayland, LinuxBackendSelectionReason.ForcedByUser, "Native Wayland explicitly selected by user", session.HasX11Display)
                : new LinuxDisplayBackendDecision(LinuxDisplayBackendKind.Unavailable, LinuxBackendSelectionReason.NoDisplay, "Wayland was requested but WAYLAND_DISPLAY is unavailable", session.HasX11Display);
        }

        if (session.HasWaylandDisplay)
        {
            return new LinuxDisplayBackendDecision(LinuxDisplayBackendKind.Wayland, LinuxBackendSelectionReason.DefaultWayland, "Native Wayland selected by default", session.HasX11Display);
        }

        if (session.HasX11Display)
        {
            return new LinuxDisplayBackendDecision(LinuxDisplayBackendKind.X11, LinuxBackendSelectionReason.MissingWaylandDisplay, "X11 fallback selected because WAYLAND_DISPLAY is unavailable", false);
        }

        return new LinuxDisplayBackendDecision(LinuxDisplayBackendKind.Unavailable, LinuxBackendSelectionReason.NoDisplay, "Neither Wayland nor X11 display is available", false);
    }
}

/// <summary>
/// Shared, thread-safe desktop backend state consumed by Host startup and the diagnostics capability matrix.
/// </summary>
public sealed class LinuxDisplayBackendState
{
    private readonly Lock _sync = new();
    private LinuxDisplayBackendDecision _decision;

    public LinuxDisplayBackendState(LinuxDesktopSessionSnapshot? session = null)
    {
        Session = session ?? LinuxDesktopSessionSnapshot.Detect();
        _decision = LinuxDisplayBackendSelector.Select(Session);
    }

    public LinuxDesktopSessionSnapshot Session { get; }

    public LinuxDisplayBackendDecision Decision
    {
        get
        {
            lock (_sync)
            {
                return _decision;
            }
        }
    }

    public bool TryMarkInitializationFallback(string exceptionType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exceptionType);
        lock (_sync)
        {
            if (_decision.Backend != LinuxDisplayBackendKind.Wayland || !_decision.CanFallbackToX11)
            {
                return false;
            }

            _decision = new LinuxDisplayBackendDecision(
                LinuxDisplayBackendKind.X11,
                LinuxBackendSelectionReason.InitializationFailure,
                $"Native Wayland initialization failed ({exceptionType}); using X11/XWayland fallback",
                false);
            return true;
        }
    }
}
