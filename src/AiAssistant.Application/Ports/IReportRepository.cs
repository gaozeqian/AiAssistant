using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public interface IReportRepository
{
    Task<DailyReport?> GetLatestAsync(DateOnly localDate, CancellationToken ct);
    Task<IReadOnlyList<DailyReport>> ListVersionsAsync(DateOnly localDate, CancellationToken ct);

    Task<DailyReport> AppendAsync(
        DateOnly localDate,
        string markdown,
        string statsJson,
        string modelId,
        ModelEndpointTier endpointTier,
        bool redactionApplied,
        ReportTriggerSource triggerSource,
        DateTimeOffset generatedAtUtc,
        CancellationToken ct);
}
