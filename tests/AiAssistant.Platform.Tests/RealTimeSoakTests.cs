using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AiAssistant.Application.Policies;
using AiAssistant.Application.Ports;
using AiAssistant.Capture;
using AiAssistant.Core.Configuration;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Platform.Windows;
using AiAssistant.Storage;
using AiAssistant.Storage.KeyStores;
using AiAssistant.Storage.Media;
using AiAssistant.Storage.Observability;
using AiAssistant.Storage.Sqlite;
using Microsoft.Extensions.Logging;

namespace AiAssistant.Platform.Tests;

/// <summary>
/// Real-wall-clock soak: continuously feeds the bounded capture pipeline, pauses/resumes capture and
/// injects blacklisted samples for the configured duration. Enable with
/// AIASSISTANT_REAL_SOAK_MINUTES=10..120; disabled by default so CI stays fast.
/// </summary>
[Collection("Soak")]
public sealed class RealTimeSoakTests : IDisposable
{
    private readonly string _tempRoot;

    public RealTimeSoakTests()
    {
        _tempRoot = Path.Combine(ArtifactPaths.Resolve(FindRepoRoot(), "tmp", "soak-real"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AiAssistant.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Ignore transient cleanup race
        }
    }

    [Fact]
    public async Task RealTimeSoak_NoControlLoss_BoundedQueue_NoLeaks_AndHealthyStorage()
    {
        var minutes = ResolveMinutes();
        if (minutes <= 0)
        {
            return;
        }

        var report = await RunAsync(TimeSpan.FromMinutes(minutes), CancellationToken.None);
        Assert.True(report.Passed, report.FailureSummary);
        Assert.Equal(0, report.EventsInsidePausedWindow);
        Assert.True(report.MaxQueueDepth <= report.QueueCapacity);
        Assert.True(report.PersistedEvents > 0);
        Assert.True(report.EncryptedBlobs > 0);
    }

    private static int ResolveMinutes()
    {
        var raw = Environment.GetEnvironmentVariable(EnvironmentNames.RealSoakMinutes);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, 1, 120)
            : 0;
    }

    private async Task<RealTimeSoakReport> RunAsync(TimeSpan duration, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var storageOptions = new StorageOptions
        {
            DataDirectory = Path.Combine(_tempRoot, "real-time-soak"),
            DefaultMediaRetention = TimeSpan.FromDays(1),
            RetentionSweepInterval = TimeSpan.Zero,
            MaxDiagnosticLogBytes = 32 * 1024
        };
        var keyStore = new StaticKeyStore(RandomNumberGenerator.GetBytes(32));
        var connectionFactory = new EncryptedSqliteConnectionFactory(storageOptions, keyStore);
        var maintenance = new SqliteDatabaseMaintenanceService(connectionFactory);
        var mediaStore = new AesGcmMediaStore(connectionFactory, keyStore);
        var eventRepo = new SqliteEventRepository(connectionFactory, mediaStore);
        var clock = new SystemClock();
        var jobQueue = new SqliteReportJobQueue(connectionFactory, clock);
        using var logProvider = new SanitizedStructuredLoggerProvider(storageOptions, clock);
        await maintenance.InitializeAsync(ct);
        await using var retention = new MediaRetentionService(
            mediaStore,
            clock,
            storageOptions,
            logProvider.CreateLogger<MediaRetentionService>());
        await retention.StartAsync(ct);

        const int queueCapacity = 12;
        var maxObservedQueueDepth = 0;
        await using var coordinator = new BoundedCaptureCoordinator(
            [],
            new SyntheticTextExtractor(),
            eventRepo,
            new PrivacyBlacklistPolicy(),
            clock,
            new CapturePipelineOptions
            {
                BoundedQueueCapacity = queueCapacity,
                DuplicateCoalesceWindow = TimeSpan.FromSeconds(15)
            },
            logProvider.CreateLogger<BoundedCaptureCoordinator>());
        coordinator.MetricsUpdated += (_, metrics) =>
        {
            int current;
            do
            {
                current = Volatile.Read(ref maxObservedQueueDepth);
                if (metrics.QueuedDataItems <= current)
                {
                    break;
                }
            }
            while (Interlocked.CompareExchange(ref maxObservedQueueDepth, metrics.QueuedDataItems, current) != current);
        };

        await coordinator.StartAsync(ct);
        await coordinator.SetBlacklistedPatternsAsync(["InternalPayrollVault"], ct);

        var samplePng = PngEncoder.EncodeBgra32(
        [
            40, 90, 200, 255, 40, 90, 200, 255,
            40, 90, 200, 255, 40, 90, 200, 255
        ], 2, 2, 8);
        const string blacklistedCanary = "FORBIDDEN_BLACKLIST_CANARY_REALTIME";
        const string pausedCanary = "FORBIDDEN_PAUSED_CANARY_REALTIME";

        var expectedControlEvents = 0;
        long expectedBlacklistDrops = 0;
        var paused = false;
        var pauseUntil = DateTimeOffset.MinValue;
        var nextPause = startedAt.AddMinutes(5);
        var pausedWindowStart = DateTimeOffset.MinValue;
        var pausedWindowEnd = DateTimeOffset.MinValue;
        var iterations = 0;

        while (DateTimeOffset.UtcNow - startedAt < duration)
        {
            ct.ThrowIfCancellationRequested();
            var now = ((IClock)clock).Now();
            await coordinator.SubmitSampleAsync(
                new CapturedSample(
                    now,
                    SourceKind.ScreenFrame,
                    "Rider",
                    $"RealTime-{iterations}",
                    ExtractedText: $"REALTIME_BODY_{iterations}",
                    RawMediaBytes: samplePng,
                    MediaKind: ContentKind.ScreenshotPng,
                    PerceptualHash: 0xBB00_0000UL + (ulong)iterations),
                ct);
            await coordinator.SubmitSampleAsync(
                new CapturedSample(now, SourceKind.WindowActivity, "Rider", $"RealTime-Window-{iterations}"),
                ct);

            if (!paused && iterations % 30 == 0)
            {
                await coordinator.SubmitSampleAsync(
                    new CapturedSample(((IClock)clock).Now(), SourceKind.WindowActivity, "InternalPayrollVault", blacklistedCanary),
                    ct);
                expectedBlacklistDrops++;
            }

            if (!paused && DateTimeOffset.UtcNow >= nextPause)
            {
                await coordinator.PauseAsync("RealTimeBreak", ct);
                expectedControlEvents++;
                paused = true;
                pauseUntil = DateTimeOffset.UtcNow.AddSeconds(20);
                pausedWindowStart = ((IClock)clock).Now().OccurredAtUtc;
                await coordinator.SubmitSampleAsync(
                    new CapturedSample(((IClock)clock).Now(), SourceKind.WindowActivity, "Browser", pausedCanary),
                    ct);
            }
            else if (paused && DateTimeOffset.UtcNow >= pauseUntil)
            {
                pausedWindowEnd = ((IClock)clock).Now().OccurredAtUtc;
                await coordinator.ResumeAsync(ct);
                expectedControlEvents++;
                paused = false;
                nextPause = DateTimeOffset.UtcNow.AddMinutes(5);
            }

            await DrainQueueAsync(coordinator, TimeSpan.FromSeconds(3), ct);
            iterations++;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        if (paused)
        {
            pausedWindowEnd = ((IClock)clock).Now().OccurredAtUtc;
            await coordinator.ResumeAsync(ct);
            expectedControlEvents++;
        }

        await DrainQueueAsync(coordinator, TimeSpan.FromSeconds(15), ct);
        var finalMetrics = coordinator.GetMetricsSnapshot();
        var health = await maintenance.CheckHealthAsync(ct);

        var allEvents = new List<ActivityEvent>();
        await foreach (var item in eventRepo.QueryAsync(new EventQuery(Limit: 100_000), ct))
        {
            allEvents.Add(item);
        }

        var controlEvents = allEvents.Count(item => item.Source == SourceKind.SystemControl);
        var eventsInsidePaused = pausedWindowStart == DateTimeOffset.MinValue
            ? 0
            : allEvents.Count(item =>
                item.Timestamp.OccurredAtUtc >= pausedWindowStart &&
                item.Timestamp.OccurredAtUtc < pausedWindowEnd &&
                item.Source != SourceKind.SystemControl);
        var encryptedBlobs = allEvents.Count(item => item.MediaRef is not null);
        var failures = new List<string>();
        if (controlEvents != expectedControlEvents)
        {
            failures.Add($"control events expected {expectedControlEvents}, actual {controlEvents}");
        }

        if (finalMetrics.QueuedDataItems != 0)
        {
            failures.Add($"queue not drained: {finalMetrics.QueuedDataItems}");
        }

        if (maxObservedQueueDepth > queueCapacity)
        {
            failures.Add($"queue depth {maxObservedQueueDepth} exceeded capacity {queueCapacity}");
        }

        if (finalMetrics.DroppedByBlacklist != expectedBlacklistDrops)
        {
            failures.Add($"blacklist drops expected {expectedBlacklistDrops}, actual {finalMetrics.DroppedByBlacklist}");
        }

        if (!health.IsHealthy || !string.Equals(health.IntegrityMessage, "ok", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"integrity check failed: {health.IntegrityMessage}");
        }

        if (eventsInsidePaused != 0)
        {
            failures.Add($"paused window persisted {eventsInsidePaused} events");
        }

        if (allEvents.Any(item => item.WindowTitle.Contains(blacklistedCanary, StringComparison.Ordinal)))
        {
            failures.Add("blacklisted canary persisted");
        }

        if (allEvents.Any(item => item.WindowTitle.Contains(pausedCanary, StringComparison.Ordinal)))
        {
            failures.Add("paused canary persisted");
        }

        var report = new RealTimeSoakReport(
            (int)duration.TotalMinutes,
            DateTimeOffset.UtcNow - startedAt,
            iterations,
            queueCapacity,
            maxObservedQueueDepth,
            expectedControlEvents,
            controlEvents,
            expectedBlacklistDrops,
            finalMetrics.DroppedByBlacklist,
            allEvents.Count,
            encryptedBlobs,
            eventsInsidePaused,
            health.IntegrityMessage,
            failures);
        await WriteReportAsync(report, ct);
        return report;
    }

    private static async Task DrainQueueAsync(
        BoundedCaptureCoordinator coordinator,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (coordinator.GetMetricsSnapshot().QueuedDataItems == 0)
            {
                return;
            }

            await Task.Delay(100, ct);
        }
    }

    private static async Task WriteReportAsync(RealTimeSoakReport report, CancellationToken ct)
    {
        var directory = ArtifactPaths.Resolve(FindRepoRoot(), "evidence", "soak");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, $"soak-real-{report.Minutes}m.md"),
            report.ToMarkdown(),
            ct);
    }

    private sealed class SyntheticTextExtractor : ITextExtractor
    {
        public CapabilityState AccessibilityCapability =>
            CapabilityState.Supported(CapabilityKind.AccessibilityText, "RealTimeSoakUIA");
        public CapabilityState OcrCapability =>
            CapabilityState.Supported(CapabilityKind.Ocr, "RealTimeSoakOCR");

        public Task<ExtractedTextResult> ExtractAsync(TextExtractionRequest request, CancellationToken ct) =>
            Task.FromResult(new ExtractedTextResult(
                $"BODY_CONTENT_FOR_{request.AppName}_{request.WindowTitle}",
                TextExtractionMethod.AccessibilityTree,
                0.96));
    }

    private sealed record RealTimeSoakReport(
        int Minutes,
        TimeSpan Duration,
        int Iterations,
        int QueueCapacity,
        int MaxQueueDepth,
        int ExpectedControlEvents,
        int ActualControlEvents,
        long ExpectedBlacklistDrops,
        long ActualBlacklistDrops,
        long PersistedEvents,
        long EncryptedBlobs,
        int EventsInsidePausedWindow,
        string IntegrityMessage,
        IReadOnlyList<string> Failures)
    {
        public bool Passed => Failures.Count == 0;

        public string FailureSummary => string.Join("; ", Failures);

        public string ToMarkdown()
        {
            var builder = new StringBuilder();
            builder.AppendLine(CultureInfo.InvariantCulture, $"# 真实时间浸泡报告（{Minutes} 分钟）");
            builder.AppendLine();
            builder.AppendLine(CultureInfo.InvariantCulture, $"- 结果：**{(Passed ? "通过" : "失败")}**");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- 实际耗时：{Duration.TotalSeconds:F1} s");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- 迭代：{Iterations} 轮（每轮 2 个样本，约 2s）");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- 有界队列：容量 {QueueCapacity}，观测峰值 {MaxQueueDepth}");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- 控制事件：期望 {ExpectedControlEvents}，实际 {ActualControlEvents}");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- 黑名单拦截：期望 {ExpectedBlacklistDrops}，实际 {ActualBlacklistDrops}");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- 持久化事件：{PersistedEvents} 条；加密媒体：{EncryptedBlobs} 个");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- 暂停窗口内事件：{EventsInsidePausedWindow} 条（应为 0）");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- 完整性检查：{IntegrityMessage}");
            if (!Passed)
            {
                builder.AppendLine();
                builder.AppendLine("## 失败项");
                foreach (var failure in Failures)
                {
                    builder.AppendLine(CultureInfo.InvariantCulture, $"- {failure}");
                }
            }

            return builder.ToString();
        }
    }
}
