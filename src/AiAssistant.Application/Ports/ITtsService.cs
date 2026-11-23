using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

/// <summary>Local TTS only; feedback must never run continuously in the background.</summary>
public interface ITtsService : IAsyncDisposable
{
    CapabilityState Capability { get; }
    Task<SpeechSynthesisResult> SynthesizeAsync(SpeechSynthesisRequest request, CancellationToken ct);
}
