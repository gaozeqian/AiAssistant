using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public sealed record ModelEndpointCapability(
    ModelEndpointTier Tier,
    string BaseUrl,
    string ChatModel,
    string EmbeddingModel,
    bool ChatAvailable,
    bool StreamingAvailable,
    bool EmbeddingsAvailable,
    bool StructuredOutputAvailable,
    bool ToolCallingAvailable,
    string Diagnostic,
    DateTimeOffset ProbedAtUtc);
