using AiAssistant.Core.Domain;

namespace AiAssistant.Application.Ports;

public sealed record SearchQueryResult(
    string Query,
    DateOnly FromDate,
    DateOnly ToDate,
    IReadOnlyList<EvidenceReference> Evidence,
    int KeywordCandidateCount,
    int VectorCandidateCount)
{
    public bool HasEvidence => Evidence.Count > 0;
}
