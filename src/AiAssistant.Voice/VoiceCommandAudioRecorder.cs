using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;
using Microsoft.Extensions.Logging;

namespace AiAssistant.Voice;

/// <summary>Opt-in encrypted retention of post-wake command audio.</summary>
internal sealed class VoiceCommandAudioRecorder(
    IMediaStore? commandAudioStore,
    VoiceSessionOptions options,
    IClock clock,
    ILogger<VoiceSessionController> logger)
{
    public bool IsAvailable => commandAudioStore is not null;

    public bool RecordsCommandAudio => IsAvailable && options.RecordCommandAudio;

    public void SetEnabled(bool enabled)
    {
        if (enabled && commandAudioStore is null)
        {
            throw new InvalidOperationException("Command audio storage is not available in this session.");
        }

        options.RecordCommandAudio = enabled;
    }

    public async Task<string?> PersistAsync(float[] samples, CancellationToken ct)
    {
        if (commandAudioStore is null || !options.RecordCommandAudio || samples.Length == 0)
        {
            return null;
        }

        try
        {
            var now = clock.UtcNow;
            var wav = CommandAudioWavEncoder.Encode(samples, 16_000);
            var reference = await commandAudioStore.PutAsync(
                new ContentWriteRequest(
                    StableId.NewContentId(now),
                    ContentKind.AudioPcm16Wav,
                    wav,
                    now,
                    now + options.CommandAudioRetention),
                ct);
            return reference.ContentId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Command audio persistence skipped ({ExceptionType}).", ex.GetType().Name);
            return null;
        }
    }
}
