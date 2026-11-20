using System.Security.Cryptography;
using System.Text;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Storage.KeyStores;
using AiAssistant.Storage.Media;
using AiAssistant.Storage.Observability;
using AiAssistant.Storage.Sqlite;
using Microsoft.Extensions.Logging;

namespace AiAssistant.Storage.Tests;

public sealed class RetentionAndObservabilityTests : IDisposable
{
    private sealed class MutableClock(DateTimeOffset initialUtc) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = initialUtc;
        public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private readonly string _tempRoot;

    public RetentionAndObservabilityTests()
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
            // Ignore transient temp cleanup locks
        }
    }

    [Fact]
    public async Task MediaRetentionService_PurgesExpiredEncryptedMedia_WhilePreservingStructuredEvents()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero));
        var options = new StorageOptions
        {
            DataDirectory = Path.Combine(_tempRoot, "retention"),
            DefaultMediaRetention = TimeSpan.FromDays(7),
            RetentionSweepInterval = TimeSpan.Zero
        };
        var key = RandomNumberGenerator.GetBytes(32);
        var keyStore = new StaticKeyStore(key);
        var factory = new EncryptedSqliteConnectionFactory(options, keyStore);
        var maintenance = new SqliteDatabaseMaintenanceService(factory);
        var mediaStore = new AesGcmMediaStore(factory, keyStore);
        var repo = new SqliteEventRepository(factory, mediaStore);
        using var logProvider = new SanitizedStructuredLoggerProvider(options, clock);

        await maintenance.InitializeAsync(CancellationToken.None);

        await using var retention = new MediaRetentionService(
            mediaStore,
            clock,
            options,
            logProvider.CreateLogger<MediaRetentionService>());
        await retention.StartAsync(CancellationToken.None);

        var ts = ((IClock)clock).Now();
        var eventId = StableId.NewEventId(ts.OccurredAtUtc);
        var rawPng = new byte[] { 0x89, 0x50, 0x4E, 0x47, 10, 20, 30, 40 };
        await repo.AppendWithMediaBatchAsync(
        [
            new EventWithMediaWrite(
                new ActivityEvent(eventId, ts, SourceKind.ScreenFrame, "Rider", "ArchitectureReview.md", extractedText: "Design notes"),
                rawPng,
                ContentKind.ScreenshotPng)
        ], CancellationToken.None);

        // Day 3: not yet expired -> sweep purges 0, media remains readable
        clock.UtcNow = clock.UtcNow.AddDays(3);
        Assert.Equal(0, await retention.RunSweepNowAsync(CancellationToken.None));
        var beforeExpiry = await repo.GetByIdAsync(eventId, CancellationToken.None);
        Assert.NotNull(beforeExpiry?.MediaRef);
        Assert.Equal(rawPng, await mediaStore.ReadDecryptedAsync(beforeExpiry!.MediaRef!.ContentId, CancellationToken.None));

        // Day 8 (> 7d retention): sweep securely erases .enc blob and unlinks content_id, while keeping structured event!
        clock.UtcNow = clock.UtcNow.AddDays(5);
        var purged = await retention.RunSweepNowAsync(CancellationToken.None);
        Assert.Equal(1, purged);

        var metrics = retention.GetMetricsSnapshot();
        Assert.Equal(1, metrics.TotalPurgedBlobs);
        Assert.True(metrics.TotalSweeps >= 3); // startup + day 3 + day 8

        Assert.Empty(Directory.GetFiles(options.MediaDirectory, "*.enc", SearchOption.AllDirectories));
        var afterExpiry = await repo.GetByIdAsync(eventId, CancellationToken.None);
        Assert.NotNull(afterExpiry);
        Assert.Equal("ArchitectureReview.md", afterExpiry!.WindowTitle);
        Assert.Equal("Design notes", afterExpiry.ExtractedText);
        Assert.Null(afterExpiry.MediaRef);
    }

    [Fact]
    public void SanitizedStructuredLogger_RedactsSecretsPathsUrlsAndExtractedText_AndRotatesBoundedFiles()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero));
        var options = new StorageOptions
        {
            DataDirectory = Path.Combine(_tempRoot, "logs-privacy"),
            MaxDiagnosticLogBytes = 4096
        };

        using var provider = new SanitizedStructuredLoggerProvider(options, clock);
        var logger = provider.CreateLogger("AiAssistant.SecurityAudit");

        const string rawSecretHex = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";
        const string sensitiveBody = "CONFIDENTIAL_SALARY_SHEET_Q4_998877";
        const string sensitiveWinPath = @"C:\Users\Arthur\Documents\TopSecretDir\Payroll2026.xlsx";
        const string sensitiveUrl = "https://intranet.company.local/hr/compensation?jwt=eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9";

        // 1. Log with structured sensitive parameter names
        logger.LogInformation(
            "Processed capture for {WindowTitle} with text {ExtractedText} and key {MasterKey}",
            "Private Window Secret",
            sensitiveBody,
            rawSecretHex);

        // 2. Log with embedded paths, URLs, and raw token strings
        logger.LogWarning(
            $"File check on {sensitiveWinPath} fetched from {sensitiveUrl} using bearer {rawSecretHex}");

        // 3. Write enough metadata entries to trigger exactly one log rotation (> 4KB, < 8KB)
        for (var i = 0; i < 36; i++)
        {
            clock.UtcNow = clock.UtcNow.AddSeconds(1);
            logger.LogInformation("Pipeline heartbeat #{Iteration}: queueDepth={Depth}", i, i % 4);
        }

        var recent = provider.GetRecentEntries(100);
        Assert.NotEmpty(recent);
        Assert.Contains(recent, e => e.SanitizedMessage.Contains("<path>/Payroll2026.xlsx", StringComparison.Ordinal));

        Assert.True(File.Exists(provider.ActiveLogFilePath));
        Assert.True(File.Exists(provider.PreviousLogFilePath));
        Assert.True(new FileInfo(provider.ActiveLogFilePath).Length <= 4096 + 512);

        var combinedDiskLogs = File.ReadAllText(provider.PreviousLogFilePath, Encoding.UTF8)
                             + File.ReadAllText(provider.ActiveLogFilePath, Encoding.UTF8);

        Assert.DoesNotContain(rawSecretHex, combinedDiskLogs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(sensitiveBody, combinedDiskLogs, StringComparison.Ordinal);
        Assert.DoesNotContain("Private Window Secret", combinedDiskLogs, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\Users\Arthur\Documents\TopSecretDir", combinedDiskLogs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("compensation?jwt=", combinedDiskLogs, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Payroll2026.xlsx", combinedDiskLogs, StringComparison.Ordinal); // Only leaf filename allowed
    }
}
