using AiAssistant.Core.Domain;

namespace AiAssistant.Application.Ports;

/// <summary>Weekly/arbitrary-range aggregation with deterministic stats and optional local model overview.</summary>
public interface IRangeReportService
{
    Task<RangeReport> GenerateAsync(DateOnly fromDate, DateOnly toDate, CancellationToken ct);
}
