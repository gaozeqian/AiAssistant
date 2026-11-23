using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public interface IRetentionService : IAsyncDisposable
{
    MediaRetentionMetrics GetMetricsSnapshot();
    Task StartAsync(CancellationToken ct);
    Task<int> RunSweepNowAsync(CancellationToken ct);
}
