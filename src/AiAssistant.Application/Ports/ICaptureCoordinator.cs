using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

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
