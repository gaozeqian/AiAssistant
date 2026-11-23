

namespace AiAssistant.Application.Ports;

/// <summary>Chat and embedding endpoints, configured independently.</summary>
public sealed record ModelConfigurationView(
    ModelEndpointView Chat,
    ModelEndpointView Embedding);
