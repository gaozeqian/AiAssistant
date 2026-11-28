namespace AiAssistant.Core.Domain;

/// <summary>
/// Versioned weekly (Monday–Sunday) report. <see cref="PayloadJson"/> keeps the serialized
/// range-report payload so export can rebuild SVG/PNG/PDF from a stored version without
/// re-running the model.
/// </summary>
public sealed record WeeklyReport(
    string Id,
    DateOnly WeekStart,
    DateOnly WeekEnd,
    int Version,
    string Markdown,
    string PayloadJson,
    string ModelId,
    ReportTriggerSource TriggerSource,
    DateTimeOffset GeneratedAtUtc);
