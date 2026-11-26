namespace AiAssistant.Application.Ports;

/// <summary>
/// Automatic daily-report policy. The time is local wall-clock time; the worker always evaluates
/// the previous local date so a midnight run has a complete day to summarize.
/// </summary>
public sealed record ReportScheduleSettings(TimeOnly DailyLocalTime, bool Enabled)
{
    /// <summary>Default: check the previous day at 00:30 local time.</summary>
    public static readonly ReportScheduleSettings Default = new(new TimeOnly(0, 30), Enabled: true);
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
