using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

/// <summary>Which capability an endpoint serves; chat and embedding may live on different addresses.</summary>
public enum ModelEndpointRole
{
    Chat = 0,
    Embedding = 1
}

public sealed record ModelEndpointCapability(
    ModelEndpointRole Role,
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
