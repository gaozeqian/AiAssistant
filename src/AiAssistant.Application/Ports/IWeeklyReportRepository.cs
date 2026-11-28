using AiAssistant.Core.Domain;

namespace AiAssistant.Application.Ports;

/// <summary>
/// Versioned store for weekly range reports, keyed by the Monday that starts the week. Kept separate
/// from <see cref="IReportRepository"/> so daily lookups never see weekly rows.
/// </summary>
public interface IWeeklyReportRepository
{
    Task<WeeklyReport?> GetLatestAsync(DateOnly weekStart, CancellationToken ct);

    Task<IReadOnlyList<WeeklyReport>> ListVersionsAsync(DateOnly weekStart, CancellationToken ct);

    Task<WeeklyReport> AppendAsync(
        DateOnly weekStart,
        DateOnly weekEnd,
        string markdown,
        string payloadJson,
        string modelId,
        ReportTriggerSource triggerSource,
        DateTimeOffset generatedAtUtc,
        CancellationToken ct);
}
