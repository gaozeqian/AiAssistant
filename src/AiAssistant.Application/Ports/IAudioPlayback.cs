using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public interface IAudioPlayback
{
    CapabilityState Capability { get; }
    Task PlayAsync(ReadOnlyMemory<float> samples, int sampleRateHz, CancellationToken ct);
}
