using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

/// <summary>
/// Streaming capture source contract (implementation.md §3.3).
/// </summary>
public interface ICaptureSource : IAsyncDisposable
{
    SourceKind Kind { get; }
    CapabilityState Capability { get; }
    IAsyncEnumerable<CapturedSample> CaptureAsync(CaptureContext context, CancellationToken ct);
}
