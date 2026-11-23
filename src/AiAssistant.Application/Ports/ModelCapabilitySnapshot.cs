using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public sealed record ModelCapabilitySnapshot(
    bool ChatAvailable,
    bool EmbeddingsAvailable,
    string? ChatModel,
    string EmbeddingModel,
    ModelEndpointTier Tier,
    string Diagnostic,
    bool StreamingAvailable = false,
    bool StructuredOutputAvailable = false,
    bool ToolCallingAvailable = false);
