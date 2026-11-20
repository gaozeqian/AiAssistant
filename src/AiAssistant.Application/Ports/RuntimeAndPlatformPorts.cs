using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public interface ICapabilityProvider
{
    string PlatformName { get; }
    IReadOnlyList<CapabilityState> GetCapabilities();
}

public interface IAutoStartService
{
    CapabilityState Capability { get; }
    Task<bool> IsEnabledAsync(CancellationToken ct);
    Task SetEnabledAsync(bool enabled, CancellationToken ct);
}

public enum VoiceSessionState
{
    Sleeping = 0,
    Listening = 1,
    Processing = 2,
    Responding = 3
}

public interface IVoiceSession : IAsyncDisposable
{
    VoiceSessionState State { get; }
    CapabilityState Capability { get; }
    event EventHandler<VoiceSessionState>? StateChanged;
    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
}

public sealed record CapturePipelineMetrics(
    bool IsPaused,
    int QueuedDataItems,
    long PersistedEvents,
    long CoalescedDuplicateFrames,
    long DroppedByBlacklist,
    long DroppedByOverload,
    long ControlCommandsProcessed);

public interface ICaptureCoordinator : IAsyncDisposable
{
    bool IsPaused { get; }
    IReadOnlyList<string> BlacklistedPatterns { get; }
    event EventHandler<CapturePipelineMetrics>? MetricsUpdated;

    CapturePipelineMetrics GetMetricsSnapshot();
    Task StartAsync(CancellationToken ct);
    Task PauseAsync(string reason, CancellationToken ct);
    Task ResumeAsync(CancellationToken ct);
    Task SetBlacklistedPatternsAsync(IReadOnlyList<string> patterns, CancellationToken ct);
    Task SubmitSampleAsync(CapturedSample sample, CancellationToken ct);
    Task CaptureImmediateSnapshotAsync(CancellationToken ct);
}
