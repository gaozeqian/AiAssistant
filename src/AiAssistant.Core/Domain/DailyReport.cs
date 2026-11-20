namespace AiAssistant.Core.Domain;

public enum ModelEndpointTier
{
    LocalOffline = 0,
    CorporateIntranet = 1,
    PublicCloud = 2
}

/// <summary>
/// Versioned daily report recording model tier and redaction metadata for auditability (DESIGN.md §4.1, §8.4).
/// </summary>
public sealed record DailyReport(
    string Id,
    DateOnly LocalDate,
    int Version,
    string Markdown,
    string StatsJson,
    string ModelId,
    ModelEndpointTier EndpointTier,
    bool RedactionApplied,
    ReportTriggerSource TriggerSource,
    DateTimeOffset GeneratedAtUtc);
