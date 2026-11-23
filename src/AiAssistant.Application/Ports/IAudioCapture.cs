using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public interface IAudioCapture : IAsyncDisposable
{
    CapabilityState Capability { get; }
    IAsyncEnumerable<AudioFrame> ReadFramesAsync(CancellationToken ct);
}
