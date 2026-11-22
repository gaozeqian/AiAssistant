using AiAssistant.Core.Configuration;
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
            env(EnvironmentNames.LinuxBackend),
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
                ? new LinuxDisplayBackendDecision(LinuxDisplayBackendKind.X11, LinuxBackendSelectionReason.ForcedByUser, "用户显式选择 X11/XWayland", false)
                : new LinuxDisplayBackendDecision(LinuxDisplayBackendKind.Unavailable, LinuxBackendSelectionReason.NoDisplay, "已请求 X11 但 DISPLAY 不可用", false);
        }

        if (string.Equals(forced, "Wayland", StringComparison.OrdinalIgnoreCase))
        {
            return session.HasWaylandDisplay
                ? new LinuxDisplayBackendDecision(LinuxDisplayBackendKind.Wayland, LinuxBackendSelectionReason.ForcedByUser, "用户显式选择原生 Wayland", session.HasX11Display)
                : new LinuxDisplayBackendDecision(LinuxDisplayBackendKind.Unavailable, LinuxBackendSelectionReason.NoDisplay, "已请求 Wayland 但 WAYLAND_DISPLAY 不可用", session.HasX11Display);
        }

        if (session.HasWaylandDisplay)
        {
            return new LinuxDisplayBackendDecision(LinuxDisplayBackendKind.Wayland, LinuxBackendSelectionReason.DefaultWayland, "默认使用原生 Wayland", session.HasX11Display);
        }

        if (session.HasX11Display)
        {
            return new LinuxDisplayBackendDecision(LinuxDisplayBackendKind.X11, LinuxBackendSelectionReason.MissingWaylandDisplay, "未检测到 Wayland，回退 X11", false);
        }

        return new LinuxDisplayBackendDecision(LinuxDisplayBackendKind.Unavailable, LinuxBackendSelectionReason.NoDisplay, "Wayland 与 X11 显示均不可用", false);
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
                $"原生 Wayland 初始化失败（{exceptionType}）；已回退 X11/XWayland",
                false);
            return true;
        }
    }
}
