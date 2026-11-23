using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

/// <summary>Single model boundary for chat and embeddings; all outbound calls pass through redaction and audit.</summary>
public interface IModelGateway
{
    CapabilityState Capability { get; }
    string ChatModelId { get; }
    string EmbeddingModelId { get; }

    Task<ModelCompletionResult> CompleteAsync(ModelCompletionRequest request, CancellationToken ct);
    Task<ModelEmbeddingResult> EmbedAsync(ModelEmbeddingRequest request, CancellationToken ct);
    Task<ModelCapabilitySnapshot> ProbeAsync(CancellationToken ct);
}
