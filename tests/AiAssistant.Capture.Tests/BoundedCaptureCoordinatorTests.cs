using AiAssistant.Application.Policies;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiAssistant.Capture.Tests;

public sealed class BoundedCaptureCoordinatorTests
{
    private sealed class FakeClock(DateTimeOffset initialUtc) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = initialUtc;
        public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class InMemoryEventRepository : IEventRepository
    {
        private readonly List<ActivityEvent> _events = [];
        private readonly Lock _sync = new();
        public TimeSpan SimulatedWriteDelay { get; set; } = TimeSpan.Zero;

        public IReadOnlyList<ActivityEvent> Snapshot()
        {
            lock (_sync)
            {
                return _events.ToArray();
            }
        }

        public async Task AppendBatchAsync(IReadOnlyList<ActivityEvent> events, CancellationToken ct)
        {
            if (SimulatedWriteDelay > TimeSpan.Zero)
            {
                await Task.Delay(SimulatedWriteDelay, ct);
            }

            lock (_sync)
            {
                _events.AddRange(events);
            }
        }

        public Task AppendWithMediaBatchAsync(IReadOnlyList<EventWithMediaWrite> items, CancellationToken ct) =>
            AppendBatchAsync(items.Select(i => i.Event).ToArray(), ct);

        public async IAsyncEnumerable<ActivityEvent> QueryAsync(
            EventQuery query,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            foreach (var ev in Snapshot())
            {
                ct.ThrowIfCancellationRequested();
                yield return ev;
            }
        }

        public Task<ActivityEvent?> GetByIdAsync(string eventId, CancellationToken ct) =>
            Task.FromResult(Snapshot().FirstOrDefault(e => e.Id == eventId));

        public Task<IReadOnlyList<DateOnly>> ListActiveDatesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DateOnly>>(Snapshot().Select(e => e.Timestamp.LocalDate).Distinct().ToArray());

        public Task<bool> DeleteEventAsync(string eventId, CancellationToken ct) => Task.FromResult(false);
        public Task<int> PurgeDateAsync(DateOnly localDate, CancellationToken ct) => Task.FromResult(0);
    }

    private sealed class FakeTextExtractor : ITextExtractor
    {
        public CapabilityState AccessibilityCapability => CapabilityState.Supported(CapabilityKind.AccessibilityText, "FakeUIA");
        public CapabilityState OcrCapability => CapabilityState.Supported(CapabilityKind.Ocr, "FakeOCR");

        public Task<ExtractedTextResult> ExtractAsync(TextExtractionRequest request, CancellationToken ct) =>
            Task.FromResult(new ExtractedTextResult($"Extracted:{request.WindowTitle}", TextExtractionMethod.AccessibilityTree, 0.99));
    }

    [Fact]
    public async Task PauseBlacklistAndDuplicateCoalescing_ObeyPrivacyAndNoBackfillRules()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 20, 1, 0, 0, TimeSpan.Zero));
        var repo = new InMemoryEventRepository();
        var policy = new PrivacyBlacklistPolicy();
        var options = new CapturePipelineOptions
        {
            BoundedQueueCapacity = 16,
            DuplicateCoalesceWindow = TimeSpan.FromSeconds(10)
        };

        await using var coordinator = new BoundedCaptureCoordinator(
            [],
            new FakeTextExtractor(),
            repo,
            policy,
            clock,
            options,
            NullLogger<BoundedCaptureCoordinator>.Instance);

        await coordinator.StartAsync(CancellationToken.None);

        // 1. Submit duplicate frames (Hamming distance <= 5) -> second must coalesce
        var ts1 = ((IClock)clock).Now();
        await coordinator.SubmitSampleAsync(
            new CapturedSample(ts1, SourceKind.ScreenFrame, "Code", "Main.cs", PerceptualHash: 0b1010_0000UL),
            CancellationToken.None);

        clock.UtcNow = clock.UtcNow.AddSeconds(2);
        await coordinator.SubmitSampleAsync(
            new CapturedSample(((IClock)clock).Now(), SourceKind.ScreenFrame, "Code", "Main.cs", PerceptualHash: 0b1010_0001UL),
            CancellationToken.None);

        // 2. Submit blacklisted app -> must drop immediately
        await coordinator.SubmitSampleAsync(
            new CapturedSample(((IClock)clock).Now(), SourceKind.WindowActivity, "1Password", "Master Vault"),
            CancellationToken.None);

        await WaitUntilAsync(() =>
        {
            var metrics = coordinator.GetMetricsSnapshot();
            return metrics.PersistedEvents >= 1 &&
                   metrics.CoalescedDuplicateFrames >= 1 &&
                   metrics.DroppedByBlacklist >= 1;
        });
        Assert.Equal(1, coordinator.GetMetricsSnapshot().CoalescedDuplicateFrames);
        Assert.Equal(1, coordinator.GetMetricsSnapshot().DroppedByBlacklist);

        // 3. Pause -> events during pause are dropped, and late pre-resume frames are NOT backfilled after Resume
        await coordinator.PauseAsync("User break", CancellationToken.None);
        var pausedSampleTime = ((IClock)clock).Now();
        await coordinator.SubmitSampleAsync(
            new CapturedSample(pausedSampleTime, SourceKind.WindowActivity, "Browser", "During Pause"),
            CancellationToken.None);

        clock.UtcNow = clock.UtcNow.AddMinutes(5);
        await coordinator.ResumeAsync(CancellationToken.None);

        // Attempt to submit a stale sample whose timestamp was inside the paused interval -> must NOT backfill
        await coordinator.SubmitSampleAsync(
            new CapturedSample(pausedSampleTime, SourceKind.WindowActivity, "Browser", "Late Stale Frame From Pause"),
            CancellationToken.None);

        // Submit fresh post-resume event -> must persist
        clock.UtcNow = clock.UtcNow.AddSeconds(1);
        await coordinator.SubmitSampleAsync(
            new CapturedSample(((IClock)clock).Now(), SourceKind.WindowActivity, "Code", "After Resume"),
            CancellationToken.None);

        await WaitUntilAsync(() => repo.Snapshot().Any(e => e.WindowTitle == "After Resume"));

        var titles = repo.Snapshot().Select(e => e.WindowTitle).ToArray();
        Assert.Contains("Main.cs", titles);
        Assert.Contains("CapturePaused", titles);
        Assert.Contains("CaptureResumed", titles);
        Assert.Contains("After Resume", titles);
        Assert.DoesNotContain("Master Vault", titles);
        Assert.DoesNotContain("During Pause", titles);
        Assert.DoesNotContain("Late Stale Frame From Pause", titles);
    }

    [Fact]
    public async Task OverloadBackpressure_CapsQueueDepth_AndNeverStarvesControlCommands()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 20, 2, 0, 0, TimeSpan.Zero));
        var repo = new InMemoryEventRepository
        {
            SimulatedWriteDelay = TimeSpan.FromMilliseconds(35)
        };

        await using var coordinator = new BoundedCaptureCoordinator(
            [],
            new FakeTextExtractor(),
            repo,
            new PrivacyBlacklistPolicy(),
            clock,
            new CapturePipelineOptions { BoundedQueueCapacity = 4 },
            NullLogger<BoundedCaptureCoordinator>.Instance);

        await coordinator.StartAsync(CancellationToken.None);

        // Flood 40 distinct samples into a capacity-4 bounded channel
        for (var i = 0; i < 40; i++)
        {
            clock.UtcNow = clock.UtcNow.AddMilliseconds(50);
            await coordinator.SubmitSampleAsync(
                new CapturedSample(((IClock)clock).Now(), SourceKind.WindowActivity, "App", $"Doc-{i}"),
                CancellationToken.None);
        }

        var midSnapshot = coordinator.GetMetricsSnapshot();
        Assert.True(midSnapshot.QueuedDataItems <= 4);
        Assert.True(midSnapshot.DroppedByOverload > 0);

        // High-priority control command (Pause) must complete promptly even while consumer was busy!
        using var pauseCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await coordinator.PauseAsync("Emergency pause under load", pauseCts.Token);

        Assert.True(coordinator.IsPaused);
        Assert.True(coordinator.GetMetricsSnapshot().ControlCommandsProcessed >= 1);
    }

    [Fact]
    public async Task EnrichmentFailure_DoesNotBlockRawEventPersistence()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 20, 3, 0, 0, TimeSpan.Zero));
        var repo = new InMemoryEventRepository();
        await using var coordinator = new BoundedCaptureCoordinator(
            [],
            new FakeTextExtractor(),
            repo,
            new PrivacyBlacklistPolicy(),
            clock,
            new CapturePipelineOptions { BoundedQueueCapacity = 4 },
            NullLogger<BoundedCaptureCoordinator>.Instance,
            enrichmentPipeline: new ThrowingEnrichmentPipeline());

        await coordinator.StartAsync(CancellationToken.None);
        await coordinator.SubmitSampleAsync(
            new CapturedSample(
                ((IClock)clock).Now(),
                SourceKind.WindowActivity,
                "Editor",
                "Raw event survives",
                ExtractedText: "raw captured text"),
            CancellationToken.None);

        await WaitUntilAsync(() => repo.Snapshot().Count == 1);
        var persisted = Assert.Single(repo.Snapshot());
        Assert.Equal("Raw event survives", persisted.WindowTitle);
        Assert.Equal("raw captured text", persisted.ExtractedText);
    }

    private sealed class ThrowingEnrichmentPipeline : IEventEnrichmentPipeline
    {
        public Task<ActivityEvent> EnrichAsync(EventProcessingRequest request, CancellationToken ct) =>
            throw new InvalidOperationException("preprocessing unavailable");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!predicate())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException("Condition was not met within timeout.");
            }

            await Task.Delay(20);
        }
    }
}
