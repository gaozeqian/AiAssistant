using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public interface ISpeakerDiarizationEngine : IAsyncDisposable
{
    CapabilityState Capability { get; }
    string ModelVersion { get; }
    Task<IReadOnlyList<SpeakerTurn>> DiarizeAsync(
        ReadOnlyMemory<float> samples,
        int sampleRateHz,
        CancellationToken ct);
}
