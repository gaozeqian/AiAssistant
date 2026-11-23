using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public interface IScreenCapture : IAsyncDisposable
{
    CapabilityState Capability { get; }
    Task<ScreenCaptureFrame?> CaptureFrameAsync(CancellationToken ct);
}
