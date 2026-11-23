using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public sealed record VoiceCommandLogRecord(
    string Id,
    DateTimeOffset OccurredAtUtc,
    string WakeWord,
    string Transcript,
    string Intent,
    string ParamsJson,
    ModelEndpointTier EndpointTier,
    string? ResponseRef,
    string? AudioContentId);
