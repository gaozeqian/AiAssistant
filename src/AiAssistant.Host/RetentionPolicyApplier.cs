using AiAssistant.Application.Ports;
using AiAssistant.Storage;
using AiAssistant.Voice;

namespace AiAssistant.Host;

/// <summary>
/// Pushes a saved retention policy into the live option objects. Both are singletons read at write
/// time (media expiry when a blob is stored, command-audio expiry when a command is recorded), so
/// applying here takes effect immediately without restarting the app.
/// </summary>
internal sealed class RetentionPolicyApplier(StorageOptions storage, VoiceSessionOptions voice)
    : IRetentionPolicyApplier
{
    public void Apply(RetentionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        storage.DefaultMediaRetention = settings.MediaRetention;
        storage.MaxStorageQuotaBytes = settings.MaxStorageBytes;
        voice.CommandAudioRetention = settings.CommandAudioRetention;
    }
}
