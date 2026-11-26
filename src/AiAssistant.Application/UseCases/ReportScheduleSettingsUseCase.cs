using AiAssistant.Application.Ports;

namespace AiAssistant.Application.UseCases;

/// <summary>
/// Reads and updates the automatic daily-report schedule. Validation stays in the application layer
/// so both the desktop UI and future callers share the same rules.
/// </summary>
public sealed class ReportScheduleSettingsUseCase(IReportScheduleSettingsStore store)
{
    public ReportScheduleSettings Get() => store.Load();

    public Task SaveAsync(TimeOnly dailyLocalTime, bool enabled, CancellationToken ct = default) =>
        store.SaveAsync(new ReportScheduleSettings(dailyLocalTime, enabled), ct);
}
