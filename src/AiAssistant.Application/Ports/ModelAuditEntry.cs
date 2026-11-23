using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public sealed record ModelAuditEntry(
    DateTimeOffset TimestampUtc,
    string Purpose,
    ModelEndpointTier Tier,
    string ModelId,
    int PromptCharacters,
    int ResponseCharacters,
    bool RedactionApplied,
    bool Succeeded,
    string? ErrorType,
    long DurationMilliseconds);
