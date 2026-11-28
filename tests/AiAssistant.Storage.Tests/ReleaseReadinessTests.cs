using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Core.Exceptions;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Storage.KeyStores;
using AiAssistant.Storage.Media;
using AiAssistant.Storage.Sqlite;
using AiAssistant.Testing.Common;

namespace AiAssistant.Storage.Tests;

/// <summary>S9 release gates: forward migration, user-initiated deletion and key-loss fault injection.</summary>
[Trait("Category", "Unit")]
public sealed class ReleaseReadinessTests : IDisposable
{
    private readonly string _tempRoot;

    public ReleaseReadinessTests()
    {
        _tempRoot = TempArtifactDirectory.CreatePath("storage");
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
            // Ignore transient OS cleanup locks in temp dir
        }
    }

    [Fact]
    public async Task SchemaUpgrade_FromV1ToCurrent_PreservesEventsAndVoiceCommands()
    {
        var storage = await CreateStorageAsync("upgrade");
        var timestamp = OccurredTimestamp.FromInstant(new DateTimeOffset(2026, 11, 20, 1, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc);
        await storage.Repository.AppendBatchAsync(
        [
            new ActivityEvent("e-upgrade", timestamp, SourceKind.WindowActivity, "Code", "升级前的事件")
        ], CancellationToken.None);

        // Rewind the schema to v1 exactly as an older build would have left it.
        await using (var connection = await storage.Factory.OpenConnectionAsync(CancellationToken.None))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO voice_commands (
                    id, occurred_at_utc, wake_word, transcript, intent, params_json, endpoint_tier, response_ref, audio_content_id, audio_cleared_at_utc
                ) VALUES ('v1-row', '2026-11-20T01:00:00.0000000+00:00', '你好时光', '暂停采集', 'PauseCapture', '{}', 0, NULL, NULL, NULL);
                ALTER TABLE voice_commands DROP COLUMN audio_content_id;
                ALTER TABLE voice_commands DROP COLUMN audio_cleared_at_utc;
                -- v4/v5 也要回退，否则重放迁移会撞上已存在的列/表。
                DROP TABLE IF EXISTS weekly_reports;
                ALTER TABLE report_jobs DROP COLUMN kind;
                DELETE FROM schema_migrations WHERE version >= 2;
                """;
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        // The Host runs the migration service at startup, not on every connection open.
        await new SqliteDatabaseMaintenanceService(storage.Factory).InitializeAsync(CancellationToken.None);
        await using var upgraded = await storage.Factory.OpenConnectionAsync(CancellationToken.None);
        var version = await SchemaMigrator.GetCurrentVersionAsync(upgraded, CancellationToken.None);
        Assert.Equal(SchemaVersion.Current.Number, version.Number);

        await using (var check = upgraded.CreateCommand())
        {
            check.CommandText = "SELECT audio_content_id, audio_cleared_at_utc FROM voice_commands WHERE id = 'v1-row';";
            await using var reader = await check.ExecuteReaderAsync(CancellationToken.None);
            Assert.True(await reader.ReadAsync(CancellationToken.None));
            Assert.True(reader.IsDBNull(0));
            Assert.True(reader.IsDBNull(1));
        }

        var restored = await storage.Repository.GetByIdAsync("e-upgrade", CancellationToken.None);
        Assert.NotNull(restored);
        Assert.Equal("升级前的事件", restored.WindowTitle);
    }

    [Fact]
    public async Task PurgeDate_RemovesOnlyThatDateIncludingMediaReportsAndJobs()
    {
        var storage = await CreateStorageAsync("purge-date");
        var reports = new SqliteReportRepository(storage.Factory);
        var jobs = new SqliteReportJobQueue(storage.Factory, new SystemClock());
        var deletion = new DataDeletionService(storage.Factory, storage.Repository);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var tomorrow = today.AddDays(1);
        await AppendWithMediaAsync(storage.Repository, today, "purge-media");
        await AppendWithMediaAsync(storage.Repository, tomorrow, "keep-media");
        await AppendReportAsync(reports, today, "# 今天");
        await AppendReportAsync(reports, tomorrow, "# 明天");
        await jobs.EnqueueAsync(new ReportJobRequest(today, ReportTriggerSource.Manual), CancellationToken.None);
        await jobs.EnqueueAsync(new ReportJobRequest(tomorrow, ReportTriggerSource.Manual), CancellationToken.None);

        var result = await deletion.PurgeDateAsync(today, CancellationToken.None);

        Assert.True(result.EventsDeleted >= 1);
        Assert.True(result.ReportsDeleted >= 1);
        Assert.True(result.JobsDeleted >= 1);
        Assert.Null(await reports.GetLatestAsync(today, CancellationToken.None));
        Assert.NotNull(await reports.GetLatestAsync(tomorrow, CancellationToken.None));

        var recentJobs = await jobs.ListRecentJobsAsync(50, CancellationToken.None);
        Assert.DoesNotContain(recentJobs, job => job.TargetDate == today);
        Assert.Contains(recentJobs, job => job.TargetDate == tomorrow);

        var mediaFiles = Directory.EnumerateFiles(storage.Factory.Options.MediaDirectory, "*.enc", SearchOption.AllDirectories).ToArray();
        Assert.Single(mediaFiles);
        var surviving = await storage.Repository.GetByIdAsync("e-keep-media", CancellationToken.None);
        Assert.NotNull(surviving?.MediaRef);
        var mediaStore = new AesGcmMediaStore(storage.Factory, new StaticKeyStore(storage.Key));
        var decrypted = await mediaStore.ReadDecryptedAsync(surviving.MediaRef.ContentId, CancellationToken.None);
        Assert.Equal("encrypted payload for keep-media", Encoding.UTF8.GetString(decrypted!));

        var dates = await storage.Repository.ListActiveDatesAsync(CancellationToken.None);
        Assert.DoesNotContain(today, dates);
        Assert.Contains(tomorrow, dates);
    }

    [Fact]
    public async Task PurgeDate_RunsOffTheCallingThread_SoTheDesktopUiStaysResponsive()
    {
        var storage = await CreateStorageAsync("purge-offload");
        var recording = new ThreadRecordingEventRepository(storage.Repository);
        var deletion = new DataDeletionService(storage.Factory, recording);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await AppendWithMediaAsync(storage.Repository, today, "offload-media");

        var callerThread = Environment.CurrentManagedThreadId;
        var result = await deletion.PurgeDateAsync(today, CancellationToken.None);

        // Microsoft.Data.Sqlite's async APIs are synchronous; running the purge inline on the
        // caller (the Avalonia UI thread) froze the window, so the core must be dispatched.
        Assert.True(result.EventsDeleted >= 1);
        Assert.NotEqual(0, recording.PurgeThreadId);
        Assert.NotEqual(callerThread, recording.PurgeThreadId);
    }

    [Fact]
    public async Task PurgeAll_RemovesEveryTableAndBlob_AndDatabaseStaysUsable()
    {
        var storage = await CreateStorageAsync("purge-all");
        var reports = new SqliteReportRepository(storage.Factory);
        var jobs = new SqliteReportJobQueue(storage.Factory, new SystemClock());
        var voiceLog = new SqliteVoiceCommandLog(storage.Factory);
        var deletion = new DataDeletionService(storage.Factory, storage.Repository);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await AppendWithMediaAsync(storage.Repository, today, "all-media-1");
        await AppendWithMediaAsync(storage.Repository, today.AddDays(-1), "all-media-2");
        await AppendReportAsync(reports, today, "# 日报");
        await jobs.EnqueueAsync(new ReportJobRequest(today, ReportTriggerSource.Manual), CancellationToken.None);
        await voiceLog.AppendAsync(
            new VoiceCommandLogRecord("voice-1", DateTimeOffset.UtcNow, "你好时光", "暂停采集", "PauseCapture", "{}", ModelEndpointTier.LocalOffline, null, null),
            CancellationToken.None);
        Assert.NotEmpty(Directory.EnumerateFiles(storage.Factory.Options.MediaDirectory, "*.enc", SearchOption.AllDirectories));

        var result = await deletion.PurgeAllAsync(CancellationToken.None);

        Assert.True(result.Total >= 4);
        Assert.Empty(await storage.Repository.ListActiveDatesAsync(CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(storage.Factory.Options.MediaDirectory, "*.enc", SearchOption.AllDirectories));
        Assert.Empty(await jobs.ListRecentJobsAsync(50, CancellationToken.None));
        Assert.Equal(0, await ScalarAsync(storage.Factory, "SELECT COUNT(*) FROM daily_reports;"));
        Assert.Equal(0, await ScalarAsync(storage.Factory, "SELECT COUNT(*) FROM voice_commands;"));
        Assert.Equal(0, await ScalarAsync(storage.Factory, "SELECT COUNT(*) FROM content_refs;"));

        // The database must remain fully usable after a purge.
        var timestamp = OccurredTimestamp.FromInstant(DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
        await storage.Repository.AppendBatchAsync(
        [
            new ActivityEvent("after-purge", timestamp, SourceKind.WindowActivity, "Code", "清理后仍可写入")
        ], CancellationToken.None);
        Assert.NotNull(await storage.Repository.GetByIdAsync("after-purge", CancellationToken.None));
    }

    [Fact]
    public async Task MissingKeyMaterial_FailsDiagnosably_WithoutCorruptingDatabase()
    {
        var options = new StorageOptions { DataDirectory = Path.Combine(_tempRoot, "key-loss") };
        var keyStore = new OsProtectedKeyStore(options);
        var factory = new EncryptedSqliteConnectionFactory(options, keyStore);
        var maintenance = new SqliteDatabaseMaintenanceService(factory);
        var mediaStore = new AesGcmMediaStore(factory, keyStore);
        var repository = new SqliteEventRepository(factory, mediaStore);
        await maintenance.InitializeAsync(CancellationToken.None);
        var timestamp = OccurredTimestamp.FromInstant(DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
        await repository.AppendBatchAsync(
        [
            new ActivityEvent("key-loss", timestamp, SourceKind.WindowActivity, "Code", "密钥丢失前的事件")
        ], CancellationToken.None);

        await using (var connection = await factory.OpenConnectionAsync(CancellationToken.None))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var beforeBytes = await File.ReadAllBytesAsync(options.DatabasePath);
        Directory.Delete(options.KeyStoreDirectory, recursive: true);

        await Assert.ThrowsAsync<StorageAuthenticationException>(() =>
            repository.ListActiveDatesAsync(CancellationToken.None));

        var afterBytes = await File.ReadAllBytesAsync(options.DatabasePath);
        Assert.Equal(beforeBytes.Length, afterBytes.Length);
        Assert.True(beforeBytes.AsSpan().SequenceEqual(afterBytes));
    }

    private async Task<StorageFixture> CreateStorageAsync(string name)
    {
        var options = new StorageOptions { DataDirectory = Path.Combine(_tempRoot, name) };
        var key = RandomNumberGenerator.GetBytes(32);
        var keyStore = new StaticKeyStore(key);
        var factory = new EncryptedSqliteConnectionFactory(options, keyStore);
        var maintenance = new SqliteDatabaseMaintenanceService(factory);
        await maintenance.InitializeAsync(CancellationToken.None);
        var mediaStore = new AesGcmMediaStore(factory, keyStore);
        return new StorageFixture(factory, new SqliteEventRepository(factory, mediaStore), key);
    }

    private static async Task AppendWithMediaAsync(SqliteEventRepository repository, DateOnly date, string contentId)
    {
        var timestamp = OccurredTimestamp.FromInstant(
            new DateTimeOffset(date.ToDateTime(new TimeOnly(1, 0)), TimeSpan.Zero),
            TimeZoneInfo.Utc);
        await repository.AppendWithMediaBatchAsync(
        [
            new EventWithMediaWrite(
                new ActivityEvent($"e-{contentId}", timestamp, SourceKind.ScreenFrame, "Code", $"媒体 {contentId}"),
                Encoding.UTF8.GetBytes($"encrypted payload for {contentId}"),
                ContentKind.ScreenshotPng,
                TimeSpan.FromDays(7))
        ], CancellationToken.None);
    }

    private static Task<DailyReport> AppendReportAsync(SqliteReportRepository reports, DateOnly date, string markdown) =>
        reports.AppendAsync(
            date,
            markdown,
            "{}",
            "fake",
            ModelEndpointTier.LocalOffline,
            true,
            ReportTriggerSource.Manual,
            DateTimeOffset.UtcNow,
            CancellationToken.None);

    private static async Task<int> ScalarAsync(EncryptedSqliteConnectionFactory factory, string sql)
    {
        await using var connection = await factory.OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(CancellationToken.None);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private sealed record StorageFixture(
        EncryptedSqliteConnectionFactory Factory,
        SqliteEventRepository Repository,
        byte[] Key);

    /// <summary>Records which thread the purge actually ran on, so an inline purge is detectable.</summary>
    private sealed class ThreadRecordingEventRepository(IEventRepository inner) : IEventRepository
    {
        public int PurgeThreadId { get; private set; }

        public Task AppendBatchAsync(IReadOnlyList<ActivityEvent> events, CancellationToken ct) =>
            inner.AppendBatchAsync(events, ct);

        public Task AppendWithMediaBatchAsync(IReadOnlyList<EventWithMediaWrite> items, CancellationToken ct) =>
            inner.AppendWithMediaBatchAsync(items, ct);

        public IAsyncEnumerable<ActivityEvent> QueryAsync(EventQuery query, CancellationToken ct) =>
            inner.QueryAsync(query, ct);

        public Task<ActivityEvent?> GetByIdAsync(string eventId, CancellationToken ct) =>
            inner.GetByIdAsync(eventId, ct);

        public Task<IReadOnlyList<DateOnly>> ListActiveDatesAsync(CancellationToken ct) =>
            inner.ListActiveDatesAsync(ct);

        public Task<bool> DeleteEventAsync(string eventId, CancellationToken ct) =>
            inner.DeleteEventAsync(eventId, ct);

        public Task<int> PurgeDateAsync(DateOnly localDate, CancellationToken ct)
        {
            PurgeThreadId = Environment.CurrentManagedThreadId;
            return inner.PurgeDateAsync(localDate, ct);
        }
    }
}
