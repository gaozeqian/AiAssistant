using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public interface IVoiceSession : IAsyncDisposable
{
    VoiceSessionState State { get; }
    CapabilityState Capability { get; }

    /// <summary>True while the wake-word pump is running; the session still reports Sleeping until a wake word.</summary>
    bool IsActive { get; }

    /// <summary>Explicit opt-in for persisting command audio; off unless the user enables it.</summary>
    bool RecordsCommandAudio { get; }

    event EventHandler<VoiceSessionState>? StateChanged;
    event EventHandler<VoiceInteraction>? Interaction;

    /// <summary>Raised when the session stops unexpectedly; the user must re-enable voice.</summary>
    event EventHandler<VoiceSessionFault>? Faulted;

    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    Task SetCommandAudioRecordingAsync(bool enabled, CancellationToken ct);
}
