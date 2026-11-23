

namespace AiAssistant.Application.Ports;

/// <summary>One model endpoint as edited from the UI (chat or embedding).</summary>
public sealed record ModelEndpointView(
    string BaseUrl,
    string Model,
    string Tier,
    bool Think = false,
    string? ThinkingLevel = null);
