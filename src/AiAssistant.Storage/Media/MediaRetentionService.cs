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

    /// <summary>
    /// The sweep is synchronous underneath (SQLite async APIs plus zero-filling each blob with an
    /// fsync), so a UI-triggered sweep would block the dispatcher for seconds. Dispatch it to the
    /// thread pool like the backup/restore paths do.
    /// </summary>
    public Task<int> RunSweepNowAsync(CancellationToken ct) =>
        Task.Run(() => RunSweepCoreAsync(ct), ct);

    private async Task<int> RunSweepCoreAsync(CancellationToken ct)
    {
        await _sweepGate.WaitAsync(ct);
        try
        {
            var nowUtc = _clock.UtcNow;
            var purged = await _mediaStore.PurgeExpiredAsync(nowUtc, ct);

            // A configured cap evicts oldest-first after expiry has done its part, so "过期或超限"
            // are both covered by the same sweep the UI triggers.
            if (_options.MaxStorageQuotaBytes is { } maxBytes)
            {
                purged += await _mediaStore.PurgeOverQuotaAsync(maxBytes, ct);
            }

            Interlocked.Increment(ref _totalSweeps);
            Interlocked.Add(ref _totalPurgedBlobs, purged);
            Interlocked.Exchange(ref _lastSweepTicksUtc, nowUtc.UtcTicks);

            if (purged > 0)
            {
                _logger.LogInformation("Retention sweep securely erased {PurgedCount} media blobs.", purged);
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
            catch (OperationCanceledException ex)
            {
                // Expected on shutdown
                AiAssistant.Application.Diagnostics.SuppressedExceptionLog.Trace("Storage.Media.MediaRetentionService.DisposeAsync", ex);
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
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            // Normal shutdown
            AiAssistant.Application.Diagnostics.SuppressedExceptionLog.Trace("Storage.Media.MediaRetentionService.RunSweepSafeAsync", ex);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Retention sweep skipped due to {ExceptionType}.", ex.GetType().Name);
        }
    }
}
