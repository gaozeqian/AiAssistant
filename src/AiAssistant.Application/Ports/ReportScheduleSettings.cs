namespace AiAssistant.Application.Ports;

/// <summary>
/// Automatic daily-report policy. The time is local wall-clock time; the worker always evaluates
/// the previous local date so a midnight run has a complete day to summarize.
/// </summary>
public sealed record ReportScheduleSettings(
    TimeOnly DailyLocalTime,
    bool Enabled,
    bool WeeklyEnabled = false,
    DayOfWeek WeeklyDay = DayOfWeek.Sunday,
    TimeOnly WeeklyLocalTime = default)
{
    /// <summary>Default: daily check at 00:30; weekly check Sunday 18:00, disabled.</summary>
    public static readonly ReportScheduleSettings Default =
        new(new TimeOnly(0, 30), Enabled: true, WeeklyEnabled: false, DayOfWeek.Sunday, new TimeOnly(18, 0));
}

/// <summary>
/// Persists the user-editable daily-report schedule. The UI saves through this port; the scheduler
/// reads the latest value on every tick so a change takes effect without restarting the app.
/// </summary>
public interface IReportScheduleSettingsStore
{
    ReportScheduleSettings Load();

    Task SaveAsync(ReportScheduleSettings settings, CancellationToken ct);
}
