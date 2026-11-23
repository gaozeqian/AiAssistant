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
using AiAssistant.Testing.Common;
using Microsoft.Extensions.Logging;

namespace AiAssistant.Soak.Tests;

/// <summary>
/// S2 Final Gate Acceptance Suite (implementation.md §4 S2 Gate, §5 S2 Acceptance, §11 DoD):
/// Validates a full 24-hour accelerated soak across midnight local-date boundaries with overload bursts,
/// quota pressure, pause/resume intervals, blacklists, media retention sweeps, and zero-leak diagnostics.
/// </summary>
[Collection("Soak")]
[Trait("Category", "Soak")]
public sealed class AcceleratedFullDaySoakTests : IDisposable
{
    private sealed class SimulatedClock(DateTimeOffset initialUtc, TimeZoneInfo localTz) : IClock
    {
        private long _utcTicks = initialUtc.UtcTicks;

        public DateTimeOffset UtcNow => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

        public TimeZoneInfo LocalTimeZone { get; } = localTz;

        public void Advance(TimeSpan delta)
        {
            Interlocked.Add(ref _utcTicks, delta.Ticks);
        }
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
        public string PlatformName => "Windows Soak Harness (win-x64)";
        public IReadOnlyList<CapabilityState> GetCapabilities() =>
        [
            CapabilityState.Supported(CapabilityKind.ScreenCapture, "Win32 GDI StretchBlt"),
            CapabilityState.Supported(CapabilityKind.WindowActivity, "Win32 ForegroundWindow"),
            keyStore.Capability
        ];
    }

    private readonly string _tempRoot;

    public AcceleratedFullDaySoakTests()
    {
        _tempRoot = TempArtifactDirectory.CreatePath("soak");
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
    public async Task FullDay24HourAcceleratedSoak_ZeroLostControlEvents_BoundedQueue_AndZeroPrivacyLeaks()
    {
        // Use UTC+8 local timezone so a 24h run starting at 09:00 local time crosses midnight into Day 2!
        var tzPlus8 = TimeZoneInfo.CreateCustomTimeZone("ChinaStandardSimulated", TimeSpan.FromHours(8), "CST", "CST");
        var startInstantUtc = new DateTimeOffset(2026, 11, 20, 1, 0, 0, TimeSpan.Zero); // 2026-11-20 09:00 +08:00
        var clock = new SimulatedClock(startInstantUtc, tzPlus8);

        var masterKeyBytes = RandomNumberGenerator.GetBytes(32);
        var masterKeyHex = Convert.ToHexStringLower(masterKeyBytes);
        var masterKeyBase64 = Convert.ToBase64String(masterKeyBytes);

        var storageOptions = new StorageOptions
        {
            DataDirectory = Path.Combine(_tempRoot, "soak-24h"),
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

        await maintenance.InitializeAsync(CancellationToken.None);

        await using var retention = new MediaRetentionService(
            mediaStore,
            clock,
            storageOptions,
            logProvider.CreateLogger<MediaRetentionService>());
        await retention.StartAsync(CancellationToken.None);

        const int queueCapacity = 12;
        var pipelineOptions = new CapturePipelineOptions
        {
            BoundedQueueCapacity = queueCapacity,
            DuplicateCoalesceWindow = TimeSpan.FromSeconds(15)
        };
        var blacklistPolicy = new PrivacyBlacklistPolicy();

        var maxObservedQueueDepth = 0;
        await using var coordinator = new BoundedCaptureCoordinator(
            [],
            new SyntheticTextExtractor(),
            eventRepo,
            blacklistPolicy,
            clock,
            pipelineOptions,
            logProvider.CreateLogger<BoundedCaptureCoordinator>());

        coordinator.MetricsUpdated += (_, m) =>
        {
            int currentMax;
            do
            {
                currentMax = Volatile.Read(ref maxObservedQueueDepth);
                if (m.QueuedDataItems <= currentMax)
                {
                    break;
                }
            } while (Interlocked.CompareExchange(ref maxObservedQueueDepth, m.QueuedDataItems, currentMax) != currentMax);
        };

        await coordinator.StartAsync(CancellationToken.None);
        await coordinator.SetBlacklistedPatternsAsync(["InternalPayrollVault"], CancellationToken.None);

        // Pre-encode a small synthetic screenshot PNG once to keep the 24h soak fast while exercising real AES-GCM I/O
        var samplePng = PngEncoder.EncodeBgra32(
        [
            40, 90, 200, 255,  40, 90, 200, 255,
            40, 90, 200, 255,  40, 90, 200, 255
        ], 2, 2, 8);

        const string blacklistedLeakCanary = "FORBIDDEN_BLACKLIST_CANARY_8821";
        const string pausedLeakCanary = "FORBIDDEN_PAUSED_CANARY_7739";
        const string extractedBodyCanary = "SENSITIVE_DOCUMENT_BODY_CANARY_5510";

        var expectedPauseEvents = 0;
        var expectedResumeEvents = 0;

        // Simulate 24 full hours of continuous desktop usage (crossing 2026-11-20 -> 2026-11-21 midnight)
        for (var hour = 0; hour < 24; hour++)
        {
            // 1a) Steady active work: emit a screenshot frame followed by a near-duplicate frame (Hamming distance = 1)
            clock.Advance(TimeSpan.FromSeconds(5));
            var baseHash = 0xAA55_AA55_0000UL + ((ulong)hour << 8);
            await coordinator.SubmitSampleAsync(
                new CapturedSample(
                    ((IClock)clock).Now(),
                    SourceKind.ScreenFrame,
                    "Rider",
                    $"Editor-Hour-{hour}",
                    ExtractedText: $"{extractedBodyCanary}_H{hour}",
                    RawMediaBytes: samplePng,
                    MediaKind: ContentKind.ScreenshotPng,
                    PerceptualHash: baseHash),
                CancellationToken.None);

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
                CancellationToken.None);

            // Allow steady-state consumer to persist the active frame & coalesce its near-duplicate before burst test
            var steadyDeadline = DateTime.UtcNow.AddSeconds(3);
            while (coordinator.GetMetricsSnapshot().QueuedDataItems > 0 && DateTime.UtcNow < steadyDeadline)
            {
                await Task.Delay(5);
            }

            // 1b) High-rate burst exceeding bounded queue capacity (30 rapid frames into capacity-12 channel)
            for (var step = 0; step < 30; step++)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                await coordinator.SubmitSampleAsync(
                    new CapturedSample(
                        ((IClock)clock).Now(),
                        SourceKind.WindowActivity,
                        "Rider",
                        $"BurstWorkspace-H{hour}-S{step}"),
                    CancellationToken.None);
            }

            // 2) Built-in and custom blacklisted windows -> must NEVER persist
            clock.Advance(TimeSpan.FromSeconds(5));
            await coordinator.SubmitSampleAsync(
                new CapturedSample(((IClock)clock).Now(), SourceKind.WindowActivity, "1Password", blacklistedLeakCanary),
                CancellationToken.None);
            await coordinator.SubmitSampleAsync(
                new CapturedSample(((IClock)clock).Now(), SourceKind.WindowActivity, "InternalPayrollVault", blacklistedLeakCanary),
                CancellationToken.None);

            // 3) Simulate temporary disk quota exhaustion during hours 7 and 19, then issue Pause/Resume!
            // Control events MUST still succeed without loss even when data quota is exhausted!
            if (hour is 7 or 19)
            {
                storageOptions.MaxStorageQuotaBytes = 128; // Smaller than existing DB -> blocks data writes
            }

            clock.Advance(TimeSpan.FromMinutes(10));
            await coordinator.PauseAsync($"Hour-{hour}-Break", CancellationToken.None);
            expectedPauseEvents++;

            // Activity while paused -> must be dropped
            clock.Advance(TimeSpan.FromMinutes(15));
            var pausedTimestamp = ((IClock)clock).Now();
            await coordinator.SubmitSampleAsync(
                new CapturedSample(pausedTimestamp, SourceKind.ScreenFrame, "Browser", pausedLeakCanary, RawMediaBytes: samplePng, MediaKind: ContentKind.ScreenshotPng),
                CancellationToken.None);

            clock.Advance(TimeSpan.FromMinutes(20));
            await coordinator.ResumeAsync(CancellationToken.None);
            expectedResumeEvents++;

            // Lift temporary quota cap if it was active
            storageOptions.MaxStorageQuotaBytes = null;

            // Late/out-of-order frame stamped during the paused interval -> must NOT backfill!
            await coordinator.SubmitSampleAsync(
                new CapturedSample(pausedTimestamp, SourceKind.WindowActivity, "Browser", pausedLeakCanary),
                CancellationToken.None);

            // Advance remainder of the hour
            clock.Advance(TimeSpan.FromMinutes(14));
        }

        // Wait until bounded data channel drains all post-soak items
        var drainDeadline = DateTime.UtcNow.AddSeconds(10);
        while (coordinator.GetMetricsSnapshot().QueuedDataItems > 0 && DateTime.UtcNow < drainDeadline)
        {
            await Task.Delay(25);
        }

        var finalMetrics = coordinator.GetMetricsSnapshot();
        Assert.Equal(0, finalMetrics.QueuedDataItems);
        Assert.True(maxObservedQueueDepth <= queueCapacity, $"Observed queue depth {maxObservedQueueDepth} exceeded cap {queueCapacity}.");
        Assert.True(finalMetrics.CoalescedDuplicateFrames > 0);
        Assert.Equal(48, finalMetrics.DroppedByBlacklist); // 2 blacklisted samples * 24 hours
        Assert.Equal(1 + expectedPauseEvents + expectedResumeEvents, finalMetrics.ControlCommandsProcessed);

        // Verify database health & integrity after 24h high-rate churn
        var health = await maintenance.CheckHealthAsync(CancellationToken.None);
        Assert.True(health.IsHealthy);
        Assert.Equal("ok", health.IntegrityMessage, ignoreCase: true);
        Assert.True(health.EncryptedBlobCount > 0);

        // Verify cross-day local_date archiving (2026-11-20 and 2026-11-21)
        var timeline = new TimelineUseCase(eventRepo, mediaStore, clock);
        var activeDates = await timeline.ListActiveDatesAsync(CancellationToken.None);
        Assert.Contains(new DateOnly(2026, 11, 20), activeDates);
        Assert.Contains(new DateOnly(2026, 11, 21), activeDates);

        // Query ALL persisted events across the entire 24h soak
        var allEvents = new List<ActivityEvent>();
        await foreach (var ev in eventRepo.QueryAsync(new EventQuery(Limit: 10_000), CancellationToken.None))
        {
            allEvents.Add(ev);
        }

        // Gate Assertion 1: ZERO lost control events across the entire 24h run (including quota-exhausted hours!)
        var pauseControlCount = allEvents.Count(e => e.Source == SourceKind.SystemControl && e.WindowTitle == "CapturePaused");
        var resumeControlCount = allEvents.Count(e => e.Source == SourceKind.SystemControl && e.WindowTitle == "CaptureResumed");
        Assert.Equal(expectedPauseEvents, pauseControlCount);
        Assert.Equal(expectedResumeEvents, resumeControlCount);

        // Gate Assertion 2: ZERO blacklisted or paused-interval events persisted
        Assert.DoesNotContain(allEvents, e => e.WindowTitle.Contains(blacklistedLeakCanary, StringComparison.Ordinal));
        Assert.DoesNotContain(allEvents, e => e.WindowTitle.Contains(pausedLeakCanary, StringComparison.Ordinal));

        // Gate Assertion 3: Diagnostics preview & structured logs contain ZERO body text, ZERO canary titles, and ZERO key material
        var diagnostics = new DiagnosticsUseCase(
            new StubCapabilityProvider(keyStore),
            maintenance,
            coordinator,
            jobQueue,
            retention,
            logProvider);
        var exportPreview = await diagnostics.BuildSanitizedExportPreviewAsync(CancellationToken.None);
        var exportPath = Path.Combine(_tempRoot, "exported-diagnostics.json");
        await diagnostics.ExportConfirmedPreviewAsync(exportPath, exportPreview, CancellationToken.None);
        Assert.True(File.Exists(exportPath));

        var diskLogs = File.Exists(logProvider.ActiveLogFilePath)
            ? await File.ReadAllTextAsync(logProvider.ActiveLogFilePath)
            : string.Empty;

        foreach (var diagnosticSurface in new[] { exportPreview, diskLogs })
        {
            Assert.DoesNotContain(masterKeyHex, diagnosticSurface, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(masterKeyBase64, diagnosticSurface, StringComparison.Ordinal);
            Assert.DoesNotContain(extractedBodyCanary, diagnosticSurface, StringComparison.Ordinal);
            Assert.DoesNotContain(blacklistedLeakCanary, diagnosticSurface, StringComparison.Ordinal);
            Assert.DoesNotContain(pausedLeakCanary, diagnosticSurface, StringComparison.Ordinal);
        }

        // Gate Assertion 4: Advance clock past 14-day media retention -> all encrypted PNGs are securely erased while events remain
        clock.Advance(TimeSpan.FromDays(15));
        var purgedBlobs = await retention.RunSweepNowAsync(CancellationToken.None);
        Assert.Equal(health.EncryptedBlobCount, purgedBlobs);
        Assert.Empty(Directory.GetFiles(storageOptions.MediaDirectory, "*.enc", SearchOption.AllDirectories));

        var postRetentionHealth = await maintenance.CheckHealthAsync(CancellationToken.None);
        Assert.True(postRetentionHealth.IsHealthy);
        Assert.Equal(0, postRetentionHealth.EncryptedBlobCount);
        Assert.Equal(allEvents.Count, postRetentionHealth.EventCount);
    }
}
