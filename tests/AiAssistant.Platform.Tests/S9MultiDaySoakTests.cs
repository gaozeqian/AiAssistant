using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AiAssistant.Application.Policies;
using AiAssistant.Application.Ports;
using AiAssistant.Application.UseCases;
using AiAssistant.Capture;
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

/// <summary>Soak scenarios run alone: they saturate the bounded pipeline and use real-time drain deadlines.</summary>
[CollectionDefinition("Soak", DisableParallelization = true)]
public sealed class SoakCollectionDefinition;

/// <summary>
/// S9 multi-day accelerated soak. Simulates 3 days (configurable up to 14 via AIASSISTANT_SOAK_DAYS) of
/// steady work, overload bursts, pause/resume, blacklists, quota exhaustion and an 8-hour suspend window,
/// then verifies zero control-event loss, zero privacy leaks, bounded queue, schema integrity and retention.
/// </summary>
[Collection("Soak")]
public sealed class S9MultiDaySoakTests : IDisposable
{
    private readonly string _tempRoot;

    public S9MultiDaySoakTests()
    {
        _tempRoot = Path.Combine(AppContext.BaseDirectory, "test-temp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
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
    public async Task MultiDayAcceleratedSoak_NoControlLoss_NoLeaks_BoundedQueue_AndCleanRetention()
    {
        var days = ResolveDays();
        var report = await SoakRunner.RunAsync(days, _tempRoot, CancellationToken.None);

        Assert.True(report.Passed, report.FailureSummary);
        Assert.Equal(days, report.SimulatedDays);
        Assert.Equal(days + 1, report.ActiveDates.Count);
        Assert.Equal(0, report.EventsInsideSuspendWindow);
        Assert.Equal(0, report.RemainingMediaFiles);
        Assert.True(report.MaxQueueDepth <= report.QueueCapacity);
        Assert.True(report.PersistedEvents > 0);
        Assert.True(report.PurgedMediaFiles > 0);
    }

    private static int ResolveDays()
    {
        var raw = Environment.GetEnvironmentVariable("AIASSISTANT_SOAK_DAYS");
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, 1, 14)
            : 3;
    }

    private sealed class SimulatedClock(DateTimeOffset initialUtc, TimeZoneInfo localTz) : IClock
    {
        private long _utcTicks = initialUtc.UtcTicks;

        public DateTimeOffset UtcNow => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

        public TimeZoneInfo LocalTimeZone { get; } = localTz;

        public void Advance(TimeSpan delta) => Interlocked.Add(ref _utcTicks, delta.Ticks);
    }

    private sealed class SyntheticTextExtractor : ITextExtractor
    {
        public CapabilityState AccessibilityCapability => CapabilityState.Supported(CapabilityKind.AccessibilityText, "SoakUIA");
        public CapabilityState OcrCapability => CapabilityState.Supported(CapabilityKind.Ocr, "SoakOCR");

        public Task<ExtractedTextResult> ExtractAsync(TextExtractionRequest request, CancellationToken ct) =>
            Task.FromResult(new ExtractedTextResult(
                $"BODY_CONTENT_FOR_{request.AppName}_{request.WindowTitle}",
                TextExtractionMethod.AccessibilityTree,
                0.96));
    }

    private sealed class StubCapabilityProvider(IKeyStore keyStore) : ICapabilityProvider
    {
        public string PlatformName => "Windows multi-day soak harness (win-x64)";
        public IReadOnlyList<CapabilityState> GetCapabilities() =>
        [
            CapabilityState.Supported(CapabilityKind.ScreenCapture, "Win32 GDI StretchBlt"),
            CapabilityState.Supported(CapabilityKind.WindowActivity, "Win32 ForegroundWindow"),
            keyStore.Capability
        ];
    }

    private sealed record SoakReport(
        int SimulatedDays,
        int SimulatedHours,
        int QueueCapacity,
        int MaxQueueDepth,
        int ExpectedControlEvents,
        int ActualControlEvents,
        long BlacklistDrops,
        long PersistedEvents,
        long EncryptedBlobs,
        int PurgedMediaFiles,
        int RemainingMediaFiles,
        int EventsInsideSuspendWindow,
        IReadOnlyList<DateOnly> ActiveDates,
        bool IntegrityOk,
        string IntegrityMessage,
        string KeyLeakCheck,
        TimeSpan Duration,
        IReadOnlyList<string> Failures)
    {
        public bool Passed => Failures.Count == 0;

        public string FailureSummary => string.Join("; ", Failures);

        public string ToMarkdown()
        {
            var builder = new StringBuilder();
            builder.AppendLine(CultureInfo.InvariantCulture, $"# 多日加速浸泡报告（{SimulatedDays} 天）");
            builder.AppendLine();
            builder.AppendLine(CultureInfo.InvariantCulture, $"- 结果：**{(Passed ? "通过" : "失败")}**");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- 模拟时长：{SimulatedDays} 天 / {SimulatedHours} 小时（加速时钟，UTC+8）");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- 实际耗时：{Duration.TotalSeconds:F1} s");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- 有界队列：容量 {QueueCapacity}，观测峰值 {MaxQueueDepth}");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- 控制事件：期望 {ExpectedControlEvents}，实际 {ActualControlEvents}");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- 黑名单拦截：{BlacklistDrops} 条");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- 持久化事件：{PersistedEvents} 条；加密媒体：{EncryptedBlobs} 个");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- 休眠窗口内事件：{EventsInsideSuspendWindow} 条（应为 0）");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- 活跃日期：{string.Join(", ", ActiveDates.Select(date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))}");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- 完整性检查：{IntegrityMessage}");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- 保留清理：擦除 {PurgedMediaFiles} 个，剩余 {RemainingMediaFiles} 个");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- 日志/导出密钥与正文检查：{KeyLeakCheck}");
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

    private static class SoakRunner
    {
        public static async Task<SoakReport> RunAsync(int days, string tempRoot, CancellationToken ct)
        {
            var startedAt = DateTime.UtcNow;
            var tzPlus8 = TimeZoneInfo.CreateCustomTimeZone("ChinaStandardSimulated", TimeSpan.FromHours(8), "CST", "CST");
            var startInstantUtc = new DateTimeOffset(2026, 11, 20, 1, 0, 0, TimeSpan.Zero); // 09:00 +08:00
            var clock = new SimulatedClock(startInstantUtc, tzPlus8);
            var masterKeyBytes = RandomNumberGenerator.GetBytes(32);
            var masterKeyHex = Convert.ToHexStringLower(masterKeyBytes);
            var masterKeyBase64 = Convert.ToBase64String(masterKeyBytes);

            var storageOptions = new StorageOptions
            {
                DataDirectory = Path.Combine(tempRoot, $"soak-{days}d"),
                DefaultMediaRetention = TimeSpan.FromDays(14),
                RetentionSweepInterval = TimeSpan.Zero,
                MaxDiagnosticLogBytes = 32 * 1024
            };

            var keyStore = new StaticKeyStore(masterKeyBytes);
            var connectionFactory = new EncryptedSqliteConnectionFactory(storageOptions, keyStore);
            var maintenance = new SqliteDatabaseMaintenanceService(connectionFactory);
            var mediaStore = new AesGcmMediaStore(connectionFactory, keyStore);
            var eventRepo = new SqliteEventRepository(connectionFactory, mediaStore);
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
            var pipelineOptions = new CapturePipelineOptions
            {
                BoundedQueueCapacity = queueCapacity,
                DuplicateCoalesceWindow = TimeSpan.FromSeconds(15)
            };
            var maxObservedQueueDepth = 0;
            await using var coordinator = new BoundedCaptureCoordinator(
                [],
                new SyntheticTextExtractor(),
                eventRepo,
                new PrivacyBlacklistPolicy(),
                clock,
                pipelineOptions,
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

            const string blacklistedCanary = "FORBIDDEN_BLACKLIST_CANARY_8821";
            const string pausedCanary = "FORBIDDEN_PAUSED_CANARY_7739";
            const string bodyCanary = "SENSITIVE_DOCUMENT_BODY_CANARY_5510";
            // Persisted control audit events come only from pause/resume; the blacklist update is a processed command without an audit row.
            var expectedControlEvents = 0;
            var totalHours = days * 24;
            var suspendStartUtc = DateTimeOffset.MinValue;
            var suspendEndUtc = DateTimeOffset.MinValue;

            for (var hour = 0; hour < totalHours; hour++)
            {
                clock.Advance(TimeSpan.FromSeconds(5));
                var baseHash = 0xAA55_AA55_0000UL + ((ulong)hour << 8);
                await coordinator.SubmitSampleAsync(
                    new CapturedSample(
                        ((IClock)clock).Now(),
                        SourceKind.ScreenFrame,
                        "Rider",
                        $"Editor-Hour-{hour}",
                        ExtractedText: $"{bodyCanary}_H{hour}",
                        RawMediaBytes: samplePng,
                        MediaKind: ContentKind.ScreenshotPng,
                        PerceptualHash: baseHash),
                    ct);
                clock.Advance(TimeSpan.FromSeconds(3));
                await coordinator.SubmitSampleAsync(
                    new CapturedSample(
                        ((IClock)clock).Now(),
                        SourceKind.ScreenFrame,
                        "Rider",
                        $"Editor-Hour-{hour}",
                        RawMediaBytes: samplePng,
                        MediaKind: ContentKind.ScreenshotPng,
                        PerceptualHash: baseHash ^ 0x1UL),
                    ct);

                await DrainQueueAsync(coordinator, TimeSpan.FromSeconds(3));

                for (var step = 0; step < 24; step++)
                {
                    clock.Advance(TimeSpan.FromSeconds(1));
                    await coordinator.SubmitSampleAsync(
                        new CapturedSample(((IClock)clock).Now(), SourceKind.WindowActivity, "Rider", $"BurstWorkspace-H{hour}-S{step}"),
                        ct);
                }

                clock.Advance(TimeSpan.FromSeconds(5));
                await coordinator.SubmitSampleAsync(
                    new CapturedSample(((IClock)clock).Now(), SourceKind.WindowActivity, "1Password", blacklistedCanary),
                    ct);
                await coordinator.SubmitSampleAsync(
                    new CapturedSample(((IClock)clock).Now(), SourceKind.WindowActivity, "InternalPayrollVault", blacklistedCanary),
                    ct);

                if (hour is 7 or 19)
                {
                    storageOptions.MaxStorageQuotaBytes = 128;
                }

                clock.Advance(TimeSpan.FromMinutes(10));
                await coordinator.PauseAsync($"Hour-{hour}-Break", ct);
                expectedControlEvents++;

                clock.Advance(TimeSpan.FromMinutes(15));
                var pausedTimestamp = ((IClock)clock).Now();
                await coordinator.SubmitSampleAsync(
                    new CapturedSample(pausedTimestamp, SourceKind.ScreenFrame, "Browser", pausedCanary, RawMediaBytes: samplePng, MediaKind: ContentKind.ScreenshotPng),
                    ct);

                clock.Advance(TimeSpan.FromMinutes(20));
                await coordinator.ResumeAsync(ct);
                expectedControlEvents++;

                storageOptions.MaxStorageQuotaBytes = null;

                await coordinator.SubmitSampleAsync(
                    new CapturedSample(pausedTimestamp, SourceKind.WindowActivity, "Browser", pausedCanary),
                    ct);

                // Simulate an 8-hour suspend once per soak: pause before sleep, no samples while suspended, resume after wake.
                if (hour == totalHours / 2)
                {
                    clock.Advance(TimeSpan.FromMinutes(5));
                    await coordinator.PauseAsync("Suspend", ct);
                    expectedControlEvents++;
                    suspendStartUtc = ((IClock)clock).Now().OccurredAtUtc;
                    clock.Advance(TimeSpan.FromHours(8));
                    suspendEndUtc = ((IClock)clock).Now().OccurredAtUtc;
                    await coordinator.ResumeAsync(ct);
                    expectedControlEvents++;
                    continue;
                }

                clock.Advance(TimeSpan.FromMinutes(14));
            }

            await DrainQueueAsync(coordinator, TimeSpan.FromSeconds(15));
            var finalMetrics = coordinator.GetMetricsSnapshot();
            var health = await maintenance.CheckHealthAsync(ct);

            var allEvents = new List<ActivityEvent>();
            await foreach (var item in eventRepo.QueryAsync(new EventQuery(Limit: 100_000), ct))
            {
                allEvents.Add(item);
            }

            var controlEvents = allEvents.Count(item => item.Source == SourceKind.SystemControl);
            var expectedControlCommands = expectedControlEvents + 1;
            var pauseCount = allEvents.Count(item => item.Source == SourceKind.SystemControl && item.WindowTitle == "CapturePaused");
            var resumeCount = allEvents.Count(item => item.Source == SourceKind.SystemControl && item.WindowTitle == "CaptureResumed");
            var eventsInsideSuspend = suspendStartUtc == DateTimeOffset.MinValue
                ? 0
                : allEvents.Count(item =>
                    item.Timestamp.OccurredAtUtc >= suspendStartUtc &&
                    item.Timestamp.OccurredAtUtc < suspendEndUtc &&
                    item.Source != SourceKind.SystemControl);

            var activeDates = await eventRepo.ListActiveDatesAsync(ct);
            var failures = new List<string>();
            if (controlEvents != expectedControlEvents)
            {
                failures.Add($"persisted control events expected {expectedControlEvents}, actual {controlEvents}");
            }

            if (finalMetrics.ControlCommandsProcessed != expectedControlCommands)
            {
                failures.Add($"processed control commands expected {expectedControlCommands}, actual {finalMetrics.ControlCommandsProcessed}");
            }

            if (pauseCount != expectedControlEvents / 2 || resumeCount != expectedControlEvents / 2)
            {
                failures.Add($"pause/resume mismatch: pause={pauseCount} resume={resumeCount} expected={expectedControlEvents / 2} each");
            }

            if (finalMetrics.QueuedDataItems != 0)
            {
                failures.Add($"queue not drained: {finalMetrics.QueuedDataItems}");
            }

            if (maxObservedQueueDepth > queueCapacity)
            {
                failures.Add($"queue depth {maxObservedQueueDepth} exceeded capacity {queueCapacity}");
            }

            if (finalMetrics.DroppedByBlacklist != 2 * totalHours)
            {
                failures.Add($"blacklist drops expected {2 * totalHours}, actual {finalMetrics.DroppedByBlacklist}");
            }

            if (finalMetrics.CoalescedDuplicateFrames == 0)
            {
                failures.Add("no duplicate frames coalesced");
            }

            if (!health.IsHealthy || !string.Equals(health.IntegrityMessage, "ok", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"integrity check failed: {health.IntegrityMessage}");
            }

            if (eventsInsideSuspend != 0)
            {
                failures.Add($"suspend window persisted {eventsInsideSuspend} events");
            }

            if (allEvents.Any(item => item.WindowTitle.Contains(blacklistedCanary, StringComparison.Ordinal)))
            {
                failures.Add("blacklisted canary persisted");
            }

            if (allEvents.Any(item => item.WindowTitle.Contains(pausedCanary, StringComparison.Ordinal)))
            {
                failures.Add("paused canary persisted");
            }

            var diagnostics = new DiagnosticsUseCase(
                new StubCapabilityProvider(keyStore),
                maintenance,
                coordinator,
                jobQueue,
                retention,
                logProvider);
            var exportPreview = await diagnostics.BuildSanitizedExportPreviewAsync(ct);
            var diskLogs = File.Exists(logProvider.ActiveLogFilePath)
                ? await File.ReadAllTextAsync(logProvider.ActiveLogFilePath, ct)
                : string.Empty;
            var leakCheck = "通过";
            foreach (var surface in new[] { exportPreview, diskLogs })
            {
                if (surface.Contains(masterKeyHex, StringComparison.OrdinalIgnoreCase) ||
                    surface.Contains(masterKeyBase64, StringComparison.Ordinal) ||
                    surface.Contains(bodyCanary, StringComparison.Ordinal) ||
                    surface.Contains(blacklistedCanary, StringComparison.Ordinal) ||
                    surface.Contains(pausedCanary, StringComparison.Ordinal))
                {
                    leakCheck = "发现泄露";
                    failures.Add("diagnostics or logs leaked sensitive content");
                    break;
                }
            }

            var encryptedBlobs = health.EncryptedBlobCount;
            clock.Advance(TimeSpan.FromDays(15));
            var purged = await retention.RunSweepNowAsync(ct);
            var remainingMedia = Directory.Exists(storageOptions.MediaDirectory)
                ? Directory.GetFiles(storageOptions.MediaDirectory, "*.enc", SearchOption.AllDirectories).Length
                : 0;
            var postRetention = await maintenance.CheckHealthAsync(ct);
            if (remainingMedia != 0)
            {
                failures.Add($"retention left {remainingMedia} media files");
            }

            if (purged != encryptedBlobs)
            {
                failures.Add($"retention purged {purged} of {encryptedBlobs} blobs");
            }

            if (postRetention.EventCount != health.EventCount)
            {
                failures.Add("retention changed event count");
            }

            var report = new SoakReport(
                days,
                totalHours,
                queueCapacity,
                maxObservedQueueDepth,
                expectedControlEvents,
                controlEvents,
                finalMetrics.DroppedByBlacklist,
                health.EventCount,
                encryptedBlobs,
                purged,
                remainingMedia,
                eventsInsideSuspend,
                activeDates,
                health.IsHealthy,
                health.IntegrityMessage,
                leakCheck,
                DateTime.UtcNow - startedAt,
                failures);

            await WriteReportAsync(report, ct);
            return report;
        }

        private static async Task DrainQueueAsync(BoundedCaptureCoordinator coordinator, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (coordinator.GetMetricsSnapshot().QueuedDataItems > 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(5);
            }
        }

        private static async Task WriteReportAsync(SoakReport report, CancellationToken ct)
        {
            try
            {
                var root = FindRepoRoot();
                if (root is null)
                {
                    return;
                }

                var directory = Path.Combine(root, "artifacts", "soak");
                Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(
                    Path.Combine(directory, $"soak-{report.SimulatedDays}d.md"),
                    report.ToMarkdown(),
                    ct);
            }
            catch (IOException)
            {
                // Report writing must never fail the soak gate.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static string? FindRepoRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AiAssistant.sln")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName;
        }
    }
}
