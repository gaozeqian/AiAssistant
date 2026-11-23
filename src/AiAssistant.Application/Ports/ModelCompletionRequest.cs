using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public sealed record ModelCompletionRequest(
    string SystemPrompt,
    string UserPrompt,
    ModelEndpointTier Tier = ModelEndpointTier.LocalOffline,
    string Purpose = "daily-report",
    double Temperature = 0.2,
    int MaxOutputTokens = 2048);
