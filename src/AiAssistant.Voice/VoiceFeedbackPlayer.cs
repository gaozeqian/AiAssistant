using AiAssistant.Application.Ports;
using Microsoft.Extensions.Logging;

namespace AiAssistant.Voice;

/// <summary>Best-effort TTS + playback feedback; failures never break the command state machine.</summary>
internal sealed class VoiceFeedbackPlayer(
    ITtsService tts,
    IAudioPlayback playback,
    ILogger<VoiceSessionController> logger)
{
    public async Task PlayAsync(string text, CancellationToken ct)
    {
        try
        {
            if (!tts.Capability.IsOperational)
            {
                return;
            }

            var speech = await tts.SynthesizeAsync(new SpeechSynthesisRequest(text), ct);
            if (speech.Samples.Length == 0 || speech.SampleRateHz <= 0)
            {
                return;
            }

            if (playback.Capability.IsOperational)
            {
                await playback.PlayAsync(speech.Samples, speech.SampleRateHz, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug("Voice feedback playback skipped ({ExceptionType}).", ex.GetType().Name);
        }
    }

    public async Task PlaySafelyAsync(string text, CancellationToken ct)
    {
        try
        {
            await PlayAsync(text, ct);
        }
        catch (OperationCanceledException ex)
        {
            // Session stopped while feedback was playing.
            AiAssistant.Application.Diagnostics.SuppressedExceptionLog.Trace("Voice.VoiceFeedbackPlayer.PlaySafelyAsync", ex);
        }
    }
}
