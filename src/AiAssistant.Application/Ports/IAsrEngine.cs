using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

/// <summary>Replaceable offline ASR engine (sherpa-onnx default, Whisper optional).</summary>
public interface IAsrEngine : IAsyncDisposable
{
    CapabilityState Capability { get; }
    Task<AudioTranscriptionResult> TranscribeAsync(AudioTranscriptionRequest request, CancellationToken ct);
}
