using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public sealed record ModelCompletionResult(
    string Text,
    string ModelId,
    ModelEndpointTier Tier,
    bool RedactionApplied,
    TimeSpan Duration);
