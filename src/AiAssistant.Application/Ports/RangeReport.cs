using AiAssistant.Core.Domain;

namespace AiAssistant.Application.Ports;

public sealed record RangeReport(
    DateOnly FromDate,
    DateOnly ToDate,
    string Markdown,
    string StatsJson,
    IReadOnlyList<DailyActivityStat> DailyStats,
    string ModelId,
    bool Degraded,
    string? DegradedReason)
{
    public int TotalEvents => DailyStats.Sum(item => item.EventCount);

    public int TotalActiveMinutes => DailyStats.Sum(item => item.ActiveMinutes);

    public int MaxDailyEvents => DailyStats.Count == 0 ? 0 : DailyStats.Max(item => item.EventCount);
}
