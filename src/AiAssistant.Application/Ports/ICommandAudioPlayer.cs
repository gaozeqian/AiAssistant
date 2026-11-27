using AiAssistant.Application.Ports;
namespace AiAssistant.Application.Ports;

/// <summary>
/// Plays a recorded command-audio blob by content id. Keeps WAV decoding and the encrypted media
/// store out of the desktop layer, which may only reference Application.
/// </summary>
public interface ICommandAudioPlayer
{
    /// <summary>Returns false when the blob is gone (retention), locked, or not decodable.</summary>
    Task<bool> PlayAsync(string contentId, CancellationToken ct);
}
