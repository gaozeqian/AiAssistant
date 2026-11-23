using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

/// <summary>Creates a fresh single-consumer microphone capture when the AudioHub has subscribers.</summary>
public interface IAudioCaptureFactory
{
    CapabilityState Capability { get; }
    IAudioCapture Create();
}
