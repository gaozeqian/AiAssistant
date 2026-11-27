using AiAssistant.Application.Ports;
using AiAssistant.Voice;

namespace AiAssistant.Host;

/// <summary>
/// Reads a recorded command blob from the encrypted media store, decodes the PCM WAV and plays it
/// through the platform playback adapter. Returns false instead of throwing when retention already
/// erased the blob or the payload is not a playable WAV.
/// </summary>
internal sealed class CommandAudioPlayer(IMediaStore mediaStore, IAudioPlayback playback) : ICommandAudioPlayer
{
    public async Task<bool> PlayAsync(string contentId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentId);
        if (!playback.Capability.IsOperational)
        {
            return false;
        }

        var wav = await mediaStore.ReadDecryptedAsync(contentId, ct);
        if (wav is null || !CommandAudioWavDecoder.TryDecode(wav, out var samples, out var sampleRateHz))
        {
            return false;
        }

        await playback.PlayAsync(samples, sampleRateHz, ct);
        return true;
    }
}
