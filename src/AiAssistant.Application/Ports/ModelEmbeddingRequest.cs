using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public sealed record ModelEmbeddingRequest(
    IReadOnlyList<string> Inputs,
    ModelEndpointTier Tier = ModelEndpointTier.LocalOffline,
    string Purpose = "event-index");
