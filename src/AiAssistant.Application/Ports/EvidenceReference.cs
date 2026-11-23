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
