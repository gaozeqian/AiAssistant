using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

/// <summary>Streaming local wake-word detector fed from the shared AudioHub.</summary>
public interface IWakeWordDetector : IDisposable
{
    CapabilityState Capability { get; }
    IReadOnlyList<WakeWordDetection> Accept(AudioFrame frame);
    void Reset();
}
