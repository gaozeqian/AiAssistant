using AiAssistant.Application.Ports;
using Microsoft.Extensions.Logging;

namespace AiAssistant.Storage.Media;

/// <summary>
/// Background retention sweep coordinator that periodically purges expired encrypted media blobs
/// while keeping structured event history intact (implementation.md S2.4).
/// </summary>
public sealed class MediaRetentionService : IRetentionService
{
    private readonly IMediaStore _mediaStore;
    private readonly IClock _clock;
    private readonly StorageOptions _options;
    private readonly ILogger<MediaRetentionService> _logger;
    private readonly SemaphoreSlim _sweepGate = new(1, 1);
    private readonly CancellationTokenSource _shutdownCts = new();
    private Task? _backgroundLoop;

    private long _totalSweeps;
    private long _totalPurgedBlobs;
    private long _lastSweepTicksUtc;
    private bool _started;

    public MediaRetentionService(
        IMediaStore mediaStore,
        IClock clock,
        StorageOptions options,
        ILogger<MediaRetentionService> logger)
    {
        ArgumentNullException.ThrowIfNull(mediaStore);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _mediaStore = mediaStore;
        _clock = clock;
        _options = options;
        _logger = logger;
    }

    public MediaRetentionMetrics GetMetricsSnapshot()
    {
        var ticks = Interlocked.Read(ref _lastSweepTicksUtc);
        DateTimeOffset? lastSweep = ticks > 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;
        return new MediaRetentionMetrics(
            Interlocked.Read(ref _totalSweeps),
            Interlocked.Read(ref _totalPurgedBlobs),
            lastSweep);
    }

    public async Task StartAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_started)
        {
            return;
        }

        _started = true;
        // Run initial startup cleanup for any media that expired while the app was offline
        await RunSweepSafeAsync(ct);

        if (_options.RetentionSweepInterval > TimeSpan.Zero)
        {
            _backgroundLoop = Task.Run(() => RunPeriodicLoopAsync(_shutdownCts.Token), CancellationToken.None);
        }
    }

    public async Task<int> RunSweepNowAsync(CancellationToken ct)
    {
        await _sweepGate.WaitAsync(ct);
        try
        {
            var nowUtc = _clock.UtcNow;
            var purged = await _mediaStore.PurgeExpiredAsync(nowUtc, ct);
            Interlocked.Increment(ref _totalSweeps);
            Interlocked.Add(ref _totalPurgedBlobs, purged);
            Interlocked.Exchange(ref _lastSweepTicksUtc, nowUtc.UtcTicks);

            if (purged > 0)
            {
                _logger.LogInformation("Retention sweep securely erased {PurgedCount} expired media blobs.", purged);
            }

            return purged;
        }
        finally
        {
            _sweepGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdownCts.CancelAsync();
        if (_backgroundLoop is not null)
        {
            try
            {
                await _backgroundLoop;
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown
            }
        }

        _shutdownCts.Dispose();
        _sweepGate.Dispose();
    }

    private async Task RunPeriodicLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.RetentionSweepInterval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            await RunSweepSafeAsync(ct);
        }
    }

    private async Task RunSweepSafeAsync(CancellationToken ct)
    {
        try
        {
            _ = await RunSweepNowAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Retention sweep skipped due to {ExceptionType}.", ex.GetType().Name);
        }
    }
}
