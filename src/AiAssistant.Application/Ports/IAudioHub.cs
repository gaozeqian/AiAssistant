using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public interface IAudioHub : IAsyncDisposable
{
    CapabilityState Capability { get; }
    int ActiveSubscriberCount { get; }
    IAsyncEnumerable<AudioFrame> SubscribeAsync(AudioSubscription subscription, CancellationToken ct);
}
