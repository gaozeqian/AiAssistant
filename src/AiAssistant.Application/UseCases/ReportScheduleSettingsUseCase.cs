using AiAssistant.Application.Ports;

namespace AiAssistant.Application.UseCases;

/// <summary>
/// Reads and updates the automatic daily-report schedule. Validation stays in the application layer
/// so both the desktop UI and future callers share the same rules.
/// </summary>
public sealed class ReportScheduleSettingsUseCase(IReportScheduleSettingsStore store)
{
    public ReportScheduleSettings Get() => store.Load();

    /// <summary>Saves the daily schedule without dropping the weekly settings that share the file.</summary>
    public Task SaveAsync(TimeOnly dailyLocalTime, bool enabled, CancellationToken ct = default) =>
        store.SaveAsync(
            store.Load() with { DailyLocalTime = dailyLocalTime, Enabled = enabled },
            ct);

    /// <summary>Saves the weekly schedule without dropping the daily settings that share the file.</summary>
    public Task SaveWeeklyAsync(bool enabled, DayOfWeek day, TimeOnly localTime, CancellationToken ct = default) =>
        store.SaveAsync(
            store.Load() with { WeeklyEnabled = enabled, WeeklyDay = day, WeeklyLocalTime = localTime },
            ct);
}
