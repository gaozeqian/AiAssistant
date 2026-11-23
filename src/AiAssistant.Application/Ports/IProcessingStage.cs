using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

/// <summary>
/// Composable preprocessing stage contract with explicit timeout &amp; cancellation (implementation.md S4.1).
/// </summary>
public interface IProcessingStage<TInput, TOutput>
{
    string StageName { get; }
    string StageVersion { get; }
    TimeSpan DefaultTimeout { get; }

    /// <summary>Declared resource claims such as "ocr", "asr" or "cpu" used for scheduling and diagnostics.</summary>
    IReadOnlyCollection<string> ResourceClaims { get; }

    Task<TOutput> ProcessAsync(TInput input, CancellationToken ct);
}
