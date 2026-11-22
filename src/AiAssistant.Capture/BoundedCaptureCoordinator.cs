using System.Threading.Channels;
using AiAssistant.Application.Policies;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;
using Microsoft.Extensions.Logging;

namespace AiAssistant.Capture;

public sealed class CapturePipelineOptions
{
    public int BoundedQueueCapacity { get; init; } = 64;
    public IReadOnlyList<string> WhitelistedDirectories { get; init; } = [];
    public TimeSpan PeriodicFallbackInterval { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan DuplicateCoalesceWindow { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan TextExtractionTimeout { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// Orchestrates capture sources through a bounded data channel and an isolated high-priority control channel.
/// Enforces pause (without backfilling paused intervals), privacy blacklists, pHash frame deduplication,
/// and strict memory bounds under overload (implementation.md §2.6, S2.2).
/// </summary>
public sealed class BoundedCaptureCoordinator : ICaptureCoordinator
{
    private enum ControlCommandKind
    {
        Pause,
        Resume,
        UpdateBlacklist
    }

    private sealed record ControlCommand(
        ControlCommandKind Kind,
        string? Reason,
        IReadOnlyList<string>? Patterns,
        TaskCompletionSource Ack);

    private readonly IReadOnlyList<ICaptureSource> _sources;
    private readonly IScreenCapture? _screenCapture;
    private readonly IWindowActivitySource? _windowActivitySource;
    private readonly ITextExtractor _textExtractor;
    private readonly IEventRepository _eventRepository;
    private readonly PrivacyBlacklistPolicy _blacklistPolicy;
    private readonly IClock _clock;
    private readonly CapturePipelineOptions _options;
    private readonly ILogger<BoundedCaptureCoordinator> _logger;
    private readonly IEventEnrichmentPipeline? _enrichmentPipeline;

    private readonly Channel<ControlCommand> _controlChannel;
    private readonly Channel<CapturedSample> _dataChannel;
    private readonly CancellationTokenSource _sourceCts = new();
    private readonly CancellationTokenSource _consumerCts = new();
    private readonly List<Task> _sourceTasks = [];
    private Task? _consumerTask;

    private volatile bool _isPaused;
    private DateTimeOffset _resumeCutoffUtc = DateTimeOffset.MinValue;
    private ulong? _lastPersistedPhash;
    private string? _lastPersistedWindowKey;
    private DateTimeOffset _lastPersistedAtUtc = DateTimeOffset.MinValue;

    private long _persistedEvents;
    private long _coalescedDuplicateFrames;
    private long _droppedByBlacklist;
    private long _droppedByOverload;
    private long _controlCommandsProcessed;
    private bool _started;

    public BoundedCaptureCoordinator(
        IEnumerable<ICaptureSource> sources,
        ITextExtractor textExtractor,
        IEventRepository eventRepository,
        PrivacyBlacklistPolicy blacklistPolicy,
        IClock clock,
        CapturePipelineOptions options,
        ILogger<BoundedCaptureCoordinator> logger,
        IScreenCapture? screenCapture = null,
        IWindowActivitySource? windowActivitySource = null,
        IEventEnrichmentPipeline? enrichmentPipeline = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(textExtractor);
        ArgumentNullException.ThrowIfNull(eventRepository);
        ArgumentNullException.ThrowIfNull(blacklistPolicy);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _sources = sources.ToArray();
        _textExtractor = textExtractor;
        _eventRepository = eventRepository;
        _blacklistPolicy = blacklistPolicy;
        _clock = clock;
        _options = options;
        _logger = logger;
        _screenCapture = screenCapture;
        _windowActivitySource = windowActivitySource;
        _enrichmentPipeline = enrichmentPipeline;

        // Control channel is never dropped by data flood; commands carry TCS acknowledgements.
        _controlChannel = Channel.CreateUnbounded<ControlCommand>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        // Bounded data channel with explicit manual overload accounting so metrics stay exact.
        _dataChannel = Channel.CreateBounded<CapturedSample>(new BoundedChannelOptions(Math.Max(1, options.BoundedQueueCapacity))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    public bool IsPaused => _isPaused;

    public IReadOnlyList<string> BlacklistedPatterns => _blacklistPolicy.GetEffectivePatterns();

    public event EventHandler<CapturePipelineMetrics>? MetricsUpdated;

    public CapturePipelineMetrics GetMetricsSnapshot() =>
        new(
            _isPaused,
            _dataChannel.Reader.Count,
            Interlocked.Read(ref _persistedEvents),
            Interlocked.Read(ref _coalescedDuplicateFrames),
            Interlocked.Read(ref _droppedByBlacklist),
            Interlocked.Read(ref _droppedByOverload),
            Interlocked.Read(ref _controlCommandsProcessed));

    public Task StartAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_started)
        {
            return Task.CompletedTask;
        }

        _started = true;
        _consumerTask = Task.Run(() => RunConsumerLoopAsync(_consumerCts.Token), CancellationToken.None);

        var captureCtx = new CaptureContext(
            _options.WhitelistedDirectories,
            _options.PeriodicFallbackInterval);

        foreach (var source in _sources)
        {
            if (!source.Capability.IsOperational)
            {
                _logger.LogInformation(
                    "Capture source {SourceKind} skipped because capability status is {Status}.",
                    source.Kind,
                    source.Capability.Status);
                continue;
            }

            _sourceTasks.Add(Task.Run(() => PumpSourceAsync(source, captureCtx, _sourceCts.Token), CancellationToken.None));
        }

        return Task.CompletedTask;
    }

    public async Task PauseAsync(string reason, CancellationToken ct)
    {
        _isPaused = true; // Immediate atomic gate at ingress
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _controlChannel.Writer.WriteAsync(new ControlCommand(ControlCommandKind.Pause, reason, null, tcs), ct);
        await tcs.Task.WaitAsync(ct);
    }

    public async Task ResumeAsync(CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _controlChannel.Writer.WriteAsync(new ControlCommand(ControlCommandKind.Resume, null, null, tcs), ct);
        await tcs.Task.WaitAsync(ct);
    }

    public async Task SetBlacklistedPatternsAsync(IReadOnlyList<string> patterns, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _controlChannel.Writer.WriteAsync(new ControlCommand(ControlCommandKind.UpdateBlacklist, null, patterns, tcs), ct);
        await tcs.Task.WaitAsync(ct);
    }

    public Task SubmitSampleAsync(CapturedSample sample, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ct.ThrowIfCancellationRequested();

        if (_isPaused)
        {
            return Task.CompletedTask;
        }

        if (_blacklistPolicy.IsExcluded(sample.AppName, sample.WindowTitle))
        {
            Interlocked.Increment(ref _droppedByBlacklist);
            NotifyMetrics();
            return Task.CompletedTask;
        }

        // Bounded ingress: if full, evict oldest frame rather than growing unbounded memory
        while (!_dataChannel.Writer.TryWrite(sample))
        {
            if (_dataChannel.Reader.TryRead(out _))
            {
                Interlocked.Increment(ref _droppedByOverload);
            }
            else
            {
                break;
            }
        }

        NotifyMetrics();
        return Task.CompletedTask;
    }

    public async Task CaptureImmediateSnapshotAsync(CancellationToken ct)
    {
        if (_isPaused)
        {
            return;
        }

        CapturedSample? sample = null;
        if (_windowActivitySource is not null && _windowActivitySource.Capability.IsOperational)
        {
            sample = await _windowActivitySource.GetCurrentForegroundAsync(ct);
        }

        sample ??= new CapturedSample(_clock.Now(), SourceKind.ScreenFrame, "Desktop", "Manual Snapshot");

        if (_screenCapture is not null && _screenCapture.Capability.IsOperational)
        {
            var frame = await _screenCapture.CaptureFrameAsync(ct);
            if (frame is not null)
            {
                sample = sample with
                {
                    Source = SourceKind.ScreenFrame,
                    RawMediaBytes = frame.EncodedPngBytes,
                    MediaKind = ContentKind.ScreenshotPng,
                    PerceptualHash = frame.PerceptualHash
                };
            }
        }

        await SubmitSampleAsync(sample, ct);
    }

    public async ValueTask DisposeAsync()
    {
        // 1. Stop capture sources first so no new frames enter the pipeline. Native sources
        // (portal/AT-SPI/PipeWire) can ignore cancellation, so never let them block app exit.
        await _sourceCts.CancelAsync();
        await WaitOrTimeoutAsync(Task.WhenAll(_sourceTasks), TimeSpan.FromSeconds(3));

        // 2. Complete channels and let consumer gracefully drain any queued high-priority control commands
        _controlChannel.Writer.TryComplete();
        _dataChannel.Writer.TryComplete();
        await _consumerCts.CancelAsync();

        if (_consumerTask is not null)
        {
            await WaitOrTimeoutAsync(_consumerTask, TimeSpan.FromSeconds(3));
        }

        foreach (var source in _sources)
        {
            await WaitOrTimeoutAsync(source.DisposeAsync().AsTask(), TimeSpan.FromSeconds(2));
        }

        if (_screenCapture is not null)
        {
            await WaitOrTimeoutAsync(_screenCapture.DisposeAsync().AsTask(), TimeSpan.FromSeconds(2));
        }

        _sourceCts.Dispose();
        _consumerCts.Dispose();
    }

    /// <summary>Bounded wait so a wedged platform adapter cannot hang application shutdown.</summary>
    private static async Task WaitOrTimeoutAsync(Task task, TimeSpan timeout)
    {
        try
        {
            await task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            // Shutdown must stay bounded even when a native source does not observe cancellation.
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation during teardown.
        }
    }

    private async Task PumpSourceAsync(ICaptureSource source, CaptureContext context, CancellationToken ct)
    {
        try
        {
            await foreach (var sample in source.CaptureAsync(context, ct))
            {
                await SubmitSampleAsync(sample, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Clean shutdown
        }
        catch (Exception ex)
        {
            // Privacy rule: log only source metadata and exception type, never captured text or secrets.
            _logger.LogWarning(
                "Capture source {SourceKind} stopped due to {ExceptionType}.",
                source.Kind,
                ex.GetType().Name);
        }
    }

    private async Task RunConsumerLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // 1. Always drain ALL pending high-priority control commands before touching data frames!
                while (_controlChannel.Reader.TryRead(out var cmd))
                {
                    await ExecuteControlCommandAsync(cmd, CancellationToken.None);
                }

                // 2. Process one data frame if available and not paused
                if (!_isPaused && _dataChannel.Reader.TryRead(out var sample))
                {
                    await ProcessSampleSafeAsync(sample, ct);
                    continue;
                }

                // 3. Wait for whichever channel becomes readable next
                var controlWait = _controlChannel.Reader.WaitToReadAsync(ct).AsTask();
                if (_isPaused)
                {
                    if (!await controlWait)
                    {
                        break;
                    }

                    continue;
                }

                var dataWait = _dataChannel.Reader.WaitToReadAsync(ct).AsTask();
                var completed = await Task.WhenAny(controlWait, dataWait);
                if (!await completed)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Fall through to drain pending control commands
        }
        finally
        {
            using var drainCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (_controlChannel.Reader.TryRead(out var remainingCmd))
            {
                await ExecuteControlCommandAsync(remainingCmd, drainCts.Token);
            }
        }
    }

    private async Task ExecuteControlCommandAsync(ControlCommand cmd, CancellationToken ct)
    {
        try
        {
            switch (cmd.Kind)
            {
                case ControlCommandKind.Pause:
                    _isPaused = true;
                    // Discard buffered pre-pause frames so paused state takes effect immediately
                    while (_dataChannel.Reader.TryRead(out _))
                    {
                    }

                    await PersistControlAuditEventAsync("CapturePaused", ct);
                    break;

                case ControlCommandKind.Resume:
                    // Record cutoff instant so any late in-flight frame from the paused interval is never backfilled
                    _resumeCutoffUtc = _clock.UtcNow;
                    _isPaused = false;
                    await PersistControlAuditEventAsync("CaptureResumed", ct);
                    break;

                case ControlCommandKind.UpdateBlacklist:
                    _blacklistPolicy.UpdateCustomPatterns(cmd.Patterns ?? []);
                    break;
            }

            Interlocked.Increment(ref _controlCommandsProcessed);
            NotifyMetrics();
            cmd.Ack.TrySetResult();
        }
        catch (Exception ex)
        {
            cmd.Ack.TrySetException(ex);
        }
    }

    private async Task PersistControlAuditEventAsync(string actionName, CancellationToken ct)
    {
        var now = _clock.Now();
        var controlEvent = new ActivityEvent(
            StableId.NewEventId(now.OccurredAtUtc),
            now,
            SourceKind.SystemControl,
            "AiAssistant",
            actionName,
            summary: actionName,
            privacy: PrivacyLevel.LocalOnly);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await _eventRepository.AppendBatchAsync([controlEvent], ct);
                Interlocked.Increment(ref _persistedEvents);
                _logger.LogInformation("Control audit event {ControlAction} persisted.", actionName);
                return;
            }
            catch (Exception ex) when (attempt < 3 && !ct.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Retrying control event {ControlAction} after {ExceptionType} (attempt {Attempt}).",
                    actionName,
                    ex.GetType().Name,
                    attempt);
                await Task.Delay(attempt * 15, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to persist control event {ControlAction}: {ExceptionType}.", actionName, ex.GetType().Name);
            }
        }
    }

    private async Task ProcessSampleSafeAsync(CapturedSample sample, CancellationToken ct)
    {
        if (sample.Timestamp.OccurredAtUtc < _resumeCutoffUtc)
        {
            // Do not backfill events from before resume!
            return;
        }

        if (_blacklistPolicy.IsExcluded(sample.AppName, sample.WindowTitle))
        {
            Interlocked.Increment(ref _droppedByBlacklist);
            NotifyMetrics();
            return;
        }

        var windowKey = $"{sample.AppName}\u001F{sample.WindowTitle}";
        if (sample.PerceptualHash.HasValue &&
            _lastPersistedPhash.HasValue &&
            string.Equals(windowKey, _lastPersistedWindowKey, StringComparison.Ordinal) &&
            (sample.Timestamp.OccurredAtUtc - _lastPersistedAtUtc) < _options.DuplicateCoalesceWindow &&
            PerceptualHash.IsNearDuplicate(sample.PerceptualHash.Value, _lastPersistedPhash.Value))
        {
            Interlocked.Increment(ref _coalescedDuplicateFrames);
            NotifyMetrics();
            return;
        }

        var extractedText = sample.ExtractedText;
        if (string.IsNullOrWhiteSpace(extractedText) && sample.Source is SourceKind.WindowActivity or SourceKind.ScreenFrame)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(_options.TextExtractionTimeout);
                var result = await _textExtractor.ExtractAsync(
                    new TextExtractionRequest(sample.NativeWindowHandle, sample.AppName, sample.WindowTitle, sample.RawMediaBytes),
                    timeoutCts.Token);
                extractedText = result.Text;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Preprocessing/OCR failure must never block raw event persistence
                _logger.LogDebug("Text extraction degraded with {ExceptionType}; saving raw event.", ex.GetType().Name);
            }
        }

        var domainEvent = new ActivityEvent(
            StableId.NewEventId(sample.Timestamp.OccurredAtUtc),
            sample.Timestamp,
            sample.Source,
            sample.AppName,
            sample.WindowTitle,
            extractedText: extractedText,
            perceptualHash: sample.PerceptualHash,
            metadata: sample.Metadata);

        if (_enrichmentPipeline is not null)
        {
            try
            {
                domainEvent = await _enrichmentPipeline.EnrichAsync(
                    new EventProcessingRequest(
                        domainEvent,
                        sample.RawMediaBytes,
                        Audio: null,
                        NativeWindowHandle: sample.NativeWindowHandle),
                    ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Preprocessing is best-effort; the original raw event must still be persisted.
                _logger.LogWarning(
                    "Event enrichment degraded with {ExceptionType}; persisting raw event.",
                    ex.GetType().Name);
            }
        }

        try
        {
            await _eventRepository.AppendWithMediaBatchAsync(
            [
                new EventWithMediaWrite(domainEvent, sample.RawMediaBytes, sample.MediaKind)
            ], ct);

            _lastPersistedPhash = sample.PerceptualHash;
            _lastPersistedWindowKey = windowKey;
            _lastPersistedAtUtc = sample.Timestamp.OccurredAtUtc;
            Interlocked.Increment(ref _persistedEvents);
            NotifyMetrics();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Failed persisting event of source {SourceKind} due to {ExceptionType}.",
                sample.Source,
                ex.GetType().Name);
        }
    }

    private void NotifyMetrics() =>
        MetricsUpdated?.Invoke(this, GetMetricsSnapshot());
}
