using AiAssistant.Core.Domain;

namespace AiAssistant.Application.Ports;

/// <summary>One retrievable piece of local evidence. <see cref="EvidenceId"/> is the citation token shown as [E#].</summary>
public sealed record EvidenceReference(
    string EvidenceId,
    string EventId,
    DateOnly LocalDate,
    DateTimeOffset OccurredAtUtc,
    string AppName,
    string Title,
    string Excerpt,
    bool HasMedia,
    string MatchedBy,
    double Score);

public sealed record SearchQueryResult(
    string Query,
    DateOnly FromDate,
    DateOnly ToDate,
    IReadOnlyList<EvidenceReference> Evidence,
    int KeywordCandidateCount,
    int VectorCandidateCount)
{
    public bool HasEvidence => Evidence.Count > 0;
}

public sealed record EvidenceQaAnswer(
    string Question,
    string Answer,
    IReadOnlyList<EvidenceReference> Evidence,
    IReadOnlyList<string> CitedEvidenceIds,
    string ModelId,
    ModelEndpointTier EndpointTier,
    bool Degraded,
    string? DegradedReason)
{
    public bool HasEvidence => Evidence.Count > 0;
}

/// <summary>Hybrid keyword + vector retrieval over local events, always time-filtered.</summary>
public interface IEvidenceSearch
{
    Task<SearchQueryResult> SearchAsync(
        string query,
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken ct);
}

/// <summary>Evidence-grounded Q&amp;A; implementations must never answer without cited evidence.</summary>
public interface IEvidenceQuestionAnswerer
{
    Task<EvidenceQaAnswer> AnswerAsync(
        string question,
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken ct);
}

public sealed record DailyActivityStat(
    DateOnly Date,
    int EventCount,
    int ActiveMinutes,
    IReadOnlyDictionary<string, int> AppCounts)
{
    /// <summary>Relative bar width (2-100) used by the weekly chart; zero-event days render as 0.</summary>
    public int BarWidthPercent(int maxEvents) =>
        maxEvents <= 0 || EventCount <= 0 ? 0 : Math.Clamp((int)Math.Round(EventCount * 100.0 / maxEvents), 2, 100);
}

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

/// <summary>Weekly/arbitrary-range aggregation with deterministic stats and optional local model overview.</summary>
public interface IRangeReportService
{
    Task<RangeReport> GenerateAsync(DateOnly fromDate, DateOnly toDate, CancellationToken ct);
}
