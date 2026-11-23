using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

/// <summary>Generates and persists a versioned report; implemented by Summarization, invoked by Scheduler.</summary>
public interface IReportGenerationRunner
{
    Task<ReportGenerationOutcome> GenerateAsync(ReportJobRequest request, CancellationToken ct);
}
