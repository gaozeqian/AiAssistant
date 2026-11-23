using AiAssistant.Core.Domain;

namespace AiAssistant.Application.Ports;

/// <summary>Evidence-grounded Q&amp;A; implementations must never answer without cited evidence.</summary>
public interface IEvidenceQuestionAnswerer
{
    Task<EvidenceQaAnswer> AnswerAsync(
        string question,
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken ct);
}
