using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

/// <summary>
/// Image-to-text OCR fallback used when the accessibility tree yields nothing.
/// Implementations stay replaceable; missing model assets only degrade OCR, never text capture.
/// </summary>
public interface IImageOcrRecognizer
{
    CapabilityState Capability { get; }

    Task<string?> RecognizeAsync(ImageFrameBuffer frame, CancellationToken ct);
}
