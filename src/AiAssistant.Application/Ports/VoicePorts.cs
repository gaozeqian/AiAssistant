using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public sealed record WakeWordDetection(
    string Keyword,
    DateTimeOffset DetectedAtUtc);

/// <summary>Streaming local wake-word detector fed from the shared AudioHub.</summary>
public interface IWakeWordDetector : IDisposable
{
    CapabilityState Capability { get; }
    IReadOnlyList<WakeWordDetection> Accept(AudioFrame frame);
    void Reset();
}

public sealed record SpeechSynthesisRequest(string Text, string Language = "zh");

public sealed record SpeechSynthesisResult(
    ReadOnlyMemory<float> Samples,
    int SampleRateHz,
    string ModelVersion);

/// <summary>Local TTS only; feedback must never run continuously in the background.</summary>
public interface ITtsService : IAsyncDisposable
{
    CapabilityState Capability { get; }
    Task<SpeechSynthesisResult> SynthesizeAsync(SpeechSynthesisRequest request, CancellationToken ct);
}

public interface IAudioPlayback
{
    CapabilityState Capability { get; }
    Task PlayAsync(ReadOnlyMemory<float> samples, int sampleRateHz, CancellationToken ct);
}

public interface IVoiceViewNavigator
{
    void ShowTimeline();
    void ShowReports();
    void RequestExit();
}

/// <summary>Decouples voice commands from Desktop views without Voice referencing Avalonia.</summary>
public sealed class VoiceNavigationHub : IVoiceViewNavigator
{
    public event EventHandler? TimelineRequested;
    public event EventHandler? ReportsRequested;
    public event EventHandler? ExitRequested;

    public void ShowTimeline() => TimelineRequested?.Invoke(this, EventArgs.Empty);
    public void ShowReports() => ReportsRequested?.Invoke(this, EventArgs.Empty);
    public void RequestExit() => ExitRequested?.Invoke(this, EventArgs.Empty);
}

public sealed record VoiceInteraction(
    VoiceSessionState State,
    string WakeWord,
    string Transcript,
    string Feedback,
    DateTimeOffset OccurredAtUtc);

public sealed record VoiceCommandLogRecord(
    string Id,
    DateTimeOffset OccurredAtUtc,
    string WakeWord,
    string Transcript,
    string Intent,
    string ParamsJson,
    ModelEndpointTier EndpointTier,
    string? ResponseRef,
    string? AudioContentId);

public interface IVoiceCommandLog
{
    Task AppendAsync(VoiceCommandLogRecord record, CancellationToken ct);
}
