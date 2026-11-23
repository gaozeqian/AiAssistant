using AiAssistant.Core.Domain;

namespace AiAssistant.Application.Ports;

public sealed record EvidenceQaAnswer(
    string Question,
    string Answer,
    IReadOnlyList<EvidenceReference> Evidence,
    IReadOnlyList<string> CitedEvidenceIds,
    string ModelId,
    ModelEndpointTier EndpointTier,
    bool Degraded,
    string? DegradedReason)
{
    public bool HasEvidence => Evidence.Count > 0;
}
