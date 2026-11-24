using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public interface IScreenCapture : IAsyncDisposable
{
    CapabilityState Capability { get; }

    /// <summary>
    /// Captures every connected display. Each frame carries its display identity; the frame whose
    /// display hosts <see cref="ScreenCaptureTarget.WindowHandle"/> is flagged as the foreground one.
    /// Adapters that can only produce a single composite image (Wayland portal, X11 root window)
    /// return exactly one frame.
    /// </summary>
    Task<IReadOnlyList<ScreenCaptureFrame>> CaptureDisplaysAsync(ScreenCaptureTarget target, CancellationToken ct);
}
