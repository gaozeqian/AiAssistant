using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Core.Exceptions;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Storage.KeyStores;
using AiAssistant.Storage.Media;
using AiAssistant.Storage.Sqlite;
using AiAssistant.Storage.Vectors;

namespace AiAssistant.Storage.Tests;

public sealed class EncryptedStorageTests : IDisposable
{
    private readonly string _tempRoot;

    public EncryptedStorageTests()
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
            // Ignore transient OS cleanup locks in temp dir
        }
    }

    [Fact]
    public async Task Sqlite3Mc_EncryptsPagesOnDisk_AndRejectsWrongKeyAndCorruptFile()
    {
        var options = new StorageOptions { DataDirectory = Path.Combine(_tempRoot, "case1") };
        var rightKey = RandomNumberGenerator.GetBytes(32);
        var wrongKey = RandomNumberGenerator.GetBytes(32);

        var rightFactory = new EncryptedSqliteConnectionFactory(options, new StaticKeyStore(rightKey));
        var maintenance = new SqliteDatabaseMaintenanceService(rightFactory);
        var mediaStore = new AesGcmMediaStore(rightFactory, new StaticKeyStore(rightKey));
        var repo = new SqliteEventRepository(rightFactory, mediaStore);

        await maintenance.InitializeAsync(CancellationToken.None);

        const string secretMarker = "SUPER_SECRET_PROJECT_CODENAME_AURORA_9941";
        var ts = OccurredTimestamp.FromInstant(DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
        await repo.AppendBatchAsync(
        [
            new ActivityEvent(
                StableId.NewEventId(ts.OccurredAtUtc),
                ts,
                SourceKind.WindowActivity,
                "Code",
                secretMarker,
                extractedText: secretMarker)
        ], CancellationToken.None);

        // Flush WAL into main DB file so raw file inspection covers all pages
        await using (var conn = await rightFactory.OpenConnectionAsync(CancellationToken.None))
        await using (var ckpt = conn.CreateCommand())
        {
            ckpt.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await ckpt.ExecuteNonQueryAsync();
        }

        var rawDbBytes = await File.ReadAllBytesAsync(options.DatabasePath);
        Assert.NotEmpty(rawDbBytes);

        // Unencrypted SQLite starts with "SQLite format 3\0"
        var plainHeader = "SQLite format 3\0"u8.ToArray();
        Assert.False(rawDbBytes.AsSpan(0, Math.Min(16, rawDbBytes.Length)).SequenceEqual(plainHeader));
        Assert.DoesNotContain(secretMarker, Encoding.UTF8.GetString(rawDbBytes), StringComparison.Ordinal);

        // Wrong key must fail with StorageAuthenticationException
        var wrongFactory = new EncryptedSqliteConnectionFactory(options, new StaticKeyStore(wrongKey));
        await Assert.ThrowsAsync<StorageAuthenticationException>(() => wrongFactory.OpenConnectionAsync());

        // Corrupted header must fail with StorageAuthenticationException / StorageIntegrityException
        var corruptOptions = new StorageOptions { DataDirectory = Path.Combine(_tempRoot, "case1-corrupt") };
        Directory.CreateDirectory(corruptOptions.DataDirectory);
        await File.WriteAllBytesAsync(corruptOptions.DatabasePath, RandomNumberGenerator.GetBytes(8192));
        var corruptFactory = new EncryptedSqliteConnectionFactory(corruptOptions, new StaticKeyStore(rightKey));
        await Assert.ThrowsAsync<StorageAuthenticationException>(() => corruptFactory.OpenConnectionAsync());
    }

    [Fact]
    public async Task OsKeyStore_SupportsWrapperRotation_AndDiagnosesRevokedPermissions()
    {
        var options = new StorageOptions { DataDirectory = Path.Combine(_tempRoot, "case2") };
        var keyStore = new OsProtectedKeyStore(options);
        var factory = new EncryptedSqliteConnectionFactory(options, keyStore);
        var maintenance = new SqliteDatabaseMaintenanceService(factory);
        var mediaStore = new AesGcmMediaStore(factory, keyStore);

        await maintenance.InitializeAsync(CancellationToken.None);
        var payload = "Encrypted-Screenshot-Bytes-PNG"u8.ToArray();
        var contentRef = await mediaStore.PutAsync(
            new ContentWriteRequest("cnt_rotate_1", ContentKind.ScreenshotPng, payload, DateTimeOffset.UtcNow),
            CancellationToken.None);

        // Rotate wrapper KEK; underlying DEK must remain intact and decrypt existing DB + media
        await keyStore.RotateWrapperKeyAsync(options.MasterKeyId, CancellationToken.None);
        var decrypted = await mediaStore.ReadDecryptedAsync(contentRef.ContentId, CancellationToken.None);
        Assert.Equal(payload, decrypted);

        // Simulate revoked OS keystore access
        keyStore.SimulatePermissionRevoked(true);
        Assert.Equal(CapabilityStatus.PermissionRequired, keyStore.Capability.Status);
        await Assert.ThrowsAsync<PlatformPermissionException>(
            () => keyStore.GetOrCreateMasterKeyAsync(options.MasterKeyId, CancellationToken.None));
    }

    [Fact]
    public async Task AtomicEventWithMedia_RollbackLeavesZeroOrphanedRowsOrMediaFiles_AndDetectsTampering()
    {
        var options = new StorageOptions { DataDirectory = Path.Combine(_tempRoot, "case3") };
        var keyStore = new StaticKeyStore(RandomNumberGenerator.GetBytes(32));
        var factory = new EncryptedSqliteConnectionFactory(options, keyStore);
        var maintenance = new SqliteDatabaseMaintenanceService(factory);
        var mediaStore = new AesGcmMediaStore(factory, keyStore);
        var repo = new SqliteEventRepository(factory, mediaStore);

        await maintenance.InitializeAsync(CancellationToken.None);

        // 1. Inject transaction interruption right before Commit
        options.FaultBeforeEventTransactionCommit = () => throw new InvalidOperationException("Simulated power loss before commit");
        var ts = OccurredTimestamp.FromInstant(DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
        var fakePng = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4, 5, 6, 7, 8 };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repo.AppendWithMediaBatchAsync(
            [
                new EventWithMediaWrite(
                    new ActivityEvent(StableId.NewEventId(ts.OccurredAtUtc), ts, SourceKind.ScreenFrame, "Explorer", "Desktop"),
                    fakePng,
                    ContentKind.ScreenshotPng)
            ], CancellationToken.None));

        var healthAfterRollback = await maintenance.CheckHealthAsync(CancellationToken.None);
        Assert.Equal(0, healthAfterRollback.EventCount);
        Assert.Equal(0, healthAfterRollback.EncryptedBlobCount);
        if (Directory.Exists(options.MediaDirectory))
        {
            Assert.Empty(Directory.EnumerateFiles(options.MediaDirectory, "*.enc", SearchOption.AllDirectories));
        }

        // 2. Remove fault, commit cleanly, verify decryption, tamper detection, and ref-counted deletion
        options.FaultBeforeEventTransactionCommit = null;
        var eventId = StableId.NewEventId(ts.OccurredAtUtc);
        await repo.AppendWithMediaBatchAsync(
        [
            new EventWithMediaWrite(
                new ActivityEvent(eventId, ts, SourceKind.ScreenFrame, "Explorer", "Desktop"),
                fakePng,
                ContentKind.ScreenshotPng)
        ], CancellationToken.None);

        var loaded = await repo.GetByIdAsync(eventId, CancellationToken.None);
        Assert.NotNull(loaded?.MediaRef);

        var encFiles = Directory.GetFiles(options.MediaDirectory, "*.enc", SearchOption.AllDirectories);
        Assert.Single(encFiles);

        // Verify raw file on disk does NOT start with PNG signature
        var diskBytes = await File.ReadAllBytesAsync(encFiles[0]);
        Assert.NotEqual(0x89, diskBytes[0]);

        var roundtrip = await mediaStore.ReadDecryptedAsync(loaded!.MediaRef!.ContentId, CancellationToken.None);
        Assert.Equal(fakePng, roundtrip);

        // Tamper 1 bit in ciphertext -> AES-GCM must throw StorageIntegrityException
        var tampered = diskBytes.ToArray();
        tampered[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(encFiles[0], tampered);
        await Assert.ThrowsAsync<StorageIntegrityException>(
            () => mediaStore.ReadDecryptedAsync(loaded.MediaRef.ContentId, CancellationToken.None));

        // Restore valid ciphertext and delete event -> ref count hits 0 and .enc file is securely erased
        await File.WriteAllBytesAsync(encFiles[0], diskBytes);
        Assert.True(await repo.DeleteEventAsync(eventId, CancellationToken.None));
        Assert.False(File.Exists(encFiles[0]));
    }

    [Fact]
    public async Task EncryptedBackupRestore_AndSchemaDowngradeProtection_WorkAsSpecified()
    {
        var options = new StorageOptions { DataDirectory = Path.Combine(_tempRoot, "case4") };
        var key = RandomNumberGenerator.GetBytes(32);
        var keyStore = new StaticKeyStore(key);
        var factory = new EncryptedSqliteConnectionFactory(options, keyStore);
        var maintenance = new SqliteDatabaseMaintenanceService(factory);
        var mediaStore = new AesGcmMediaStore(factory, keyStore);
        var repo = new SqliteEventRepository(factory, mediaStore);

        await maintenance.InitializeAsync(CancellationToken.None);

        var ts = OccurredTimestamp.FromInstant(DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
        var evId = StableId.NewEventId(ts.OccurredAtUtc);
        await repo.AppendBatchAsync(
        [
            new ActivityEvent(evId, ts, SourceKind.WindowActivity, "Terminal", "git status")
        ], CancellationToken.None);

        var backupPath = Path.Combine(_tempRoot, "backups", "daily.backup.enc.db");
        await maintenance.BackupEncryptedAsync(backupPath, CancellationToken.None);

        // Backup must also be encrypted (wrong key fails)
        var wrongKeyFactory = new EncryptedSqliteConnectionFactory(options, new StaticKeyStore(RandomNumberGenerator.GetBytes(32)));
        await Assert.ThrowsAsync<StorageAuthenticationException>(
            () => wrongKeyFactory.OpenConnectionForPathAsync(backupPath, CancellationToken.None));

        // Purge live DB, then restore from encrypted backup
        await repo.PurgeDateAsync(ts.LocalDate, CancellationToken.None);
        Assert.Null(await repo.GetByIdAsync(evId, CancellationToken.None));

        await maintenance.RestoreEncryptedAsync(backupPath, CancellationToken.None);
        var restored = await repo.GetByIdAsync(evId, CancellationToken.None);
        Assert.NotNull(restored);
        Assert.Equal("git status", restored!.WindowTitle);

        // Insert future schema version 999 -> InitializeAsync must refuse downgrade write
        await using (var conn = await factory.OpenConnectionAsync(CancellationToken.None))
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO schema_migrations(version, name, applied_at_utc) VALUES (999, 'FutureSchema', '2027-01-01T00:00:00Z');";
            await cmd.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<SchemaMigrationException>(() => maintenance.InitializeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task BackupIsSelfContained_AndRestoresIntoColdDirectory()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var dirA = Path.Combine(_tempRoot, "full-src");
        var keyStoreA = new StaticKeyStore(key);
        var factoryA = new EncryptedSqliteConnectionFactory(new StorageOptions { DataDirectory = dirA }, keyStoreA);
        var mediaA = new AesGcmMediaStore(factoryA, keyStoreA);
        var repoA = new SqliteEventRepository(factoryA, mediaA);
        var maintA = new SqliteDatabaseMaintenanceService(factoryA);
        await maintA.InitializeAsync(CancellationToken.None);

        var ts = OccurredTimestamp.FromInstant(DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
        var evId = StableId.NewEventId(ts.OccurredAtUtc);
        await repoA.AppendBatchAsync(
            [new ActivityEvent(evId, ts, SourceKind.WindowActivity, "Terminal", "cross-dir backup")],
            CancellationToken.None);
        await mediaA.PutAsync(
            new ContentWriteRequest("content-1", ContentKind.ScreenshotPng, new byte[] { 0x89, 0x50, 0x4E, 0x47 }.AsMemory(), DateTimeOffset.UtcNow),
            CancellationToken.None);

        var backupPath = Path.Combine(_tempRoot, "full.aia-backup.zip");
        await maintA.BackupEncryptedAsync(backupPath, CancellationToken.None);

        // The archive must be self-contained: database + encrypted media. The keystore envelope only
        // exists for OS-backed key stores (StaticKeyStore keeps the key in memory, so no files to carry).
        using (var zip = ZipFile.OpenRead(backupPath))
        {
            var names = zip.Entries.Select(e => e.FullName).ToArray();
            Assert.Contains("database/aiassistant.enc.db", names);
            Assert.Contains(names, n => n.StartsWith("media/", StringComparison.Ordinal));
        }

        // Restore into a cold directory with a fresh key store instance (same OS user).
        var dirB = Path.Combine(_tempRoot, "full-dst");
        var factoryB = new EncryptedSqliteConnectionFactory(new StorageOptions { DataDirectory = dirB }, new StaticKeyStore(key));
        var mediaB = new AesGcmMediaStore(factoryB, new StaticKeyStore(key));
        var repoB = new SqliteEventRepository(factoryB, mediaB);
        var maintB = new SqliteDatabaseMaintenanceService(factoryB);
        await maintB.RestoreEncryptedAsync(backupPath, CancellationToken.None);

        var restored = await repoB.GetByIdAsync(evId, CancellationToken.None);
        Assert.NotNull(restored);
        Assert.Equal("cross-dir backup", restored!.WindowTitle);

        var health = await maintB.CheckHealthAsync(CancellationToken.None);
        Assert.Equal(1, health.EncryptedBlobCount);
    }

    [Fact]
    public async Task SqliteVecPoc_LoadsAlongsideEncryptedSqlite_SupportsSearchAndModelRebuild()
    {
        var options = new StorageOptions { DataDirectory = Path.Combine(_tempRoot, "case5-vec") };
        var keyStore = new StaticKeyStore(RandomNumberGenerator.GetBytes(32));
        var factory = new EncryptedSqliteConnectionFactory(options, keyStore);
        var maintenance = new SqliteDatabaseMaintenanceService(factory);
        var mediaStore = new AesGcmMediaStore(factory, keyStore);
        var repo = new SqliteEventRepository(factory, mediaStore);
        var vectorIndex = new HybridSqliteVectorIndex(factory);

        await maintenance.InitializeAsync(CancellationToken.None);

        var ts = OccurredTimestamp.FromInstant(DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
        var evCoding = new ActivityEvent("evt_coding", ts, SourceKind.WindowActivity, "Rider", "SqliteEventRepository.cs");
        var evMeeting = new ActivityEvent("evt_meeting", ts, SourceKind.MicrophoneAudio, "TencentMeeting", "Architecture Sync");
        await repo.AppendBatchAsync([evCoding, evMeeting], CancellationToken.None);

        var modelV1 = new VectorModelDescriptor("embeddinggemma-2:740m", "1.0", Dimensions: 4);
        await vectorIndex.UpsertAsync("evt_coding", new float[] { 0.95f, 0.10f, 0.0f, 0.0f }, modelV1, CancellationToken.None);
        await vectorIndex.UpsertAsync("evt_meeting", new float[] { 0.05f, 0.98f, 0.0f, 0.0f }, modelV1, CancellationToken.None);

        var meta = await vectorIndex.GetMetadataAsync(CancellationToken.None);
        Assert.Equal(2, meta.IndexedCount);
        Assert.Equal("embeddinggemma-2:740m", meta.ActiveModel?.ModelId);
        Assert.False(string.IsNullOrWhiteSpace(meta.BackendName));

        var hits = await vectorIndex.SearchAsync(new float[] { 1.0f, 0.0f, 0.0f, 0.0f }, topK: 2, CancellationToken.None);
        Assert.Equal(2, hits.Count);
        Assert.Equal("evt_coding", hits[0].EventId);
        Assert.True(hits[0].Score > hits[1].Score);

        // Changing model or dimensions without RebuildAsync must fail fast
        var modelV2 = new VectorModelDescriptor("bge-m3", "2.0", Dimensions: 6);
        await Assert.ThrowsAsync<StorageIntegrityException>(() =>
            vectorIndex.UpsertAsync("evt_coding", new float[] { 1, 0, 0, 0, 0, 0 }, modelV2, CancellationToken.None));

        // Explicit RebuildAsync switches model & dimensions cleanly
        await vectorIndex.RebuildAsync(modelV2, GenerateV2VectorsAsync, CancellationToken.None);
        var rebuiltHits = await vectorIndex.SearchAsync(new float[] { 0, 1, 0, 0, 0, 0 }, topK: 1, CancellationToken.None);
        Assert.Single(rebuiltHits);
        Assert.Equal("evt_meeting", rebuiltHits[0].EventId);

        static async IAsyncEnumerable<(string EventId, ReadOnlyMemory<float> Embedding)> GenerateV2VectorsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return ("evt_coding", new float[] { 1, 0, 0, 0, 0, 0 });
            yield return ("evt_meeting", new float[] { 0, 1, 0, 0, 0, 0 });
        }
    }

    [Fact]
    public async Task DiskFullQuotaFault_ThrowsStorageCapacityException_WithoutCorruptingDatabase()
    {
        var options = new StorageOptions { DataDirectory = Path.Combine(_tempRoot, "case6-quota") };
        var keyStore = new StaticKeyStore(RandomNumberGenerator.GetBytes(32));
        var factory = new EncryptedSqliteConnectionFactory(options, keyStore);
        var maintenance = new SqliteDatabaseMaintenanceService(factory);
        var mediaStore = new AesGcmMediaStore(factory, keyStore);
        var repo = new SqliteEventRepository(factory, mediaStore);

        await maintenance.InitializeAsync(CancellationToken.None);

        // Simulate disk almost full (quota = 1 KB)
        options.MaxStorageQuotaBytes = 1024;
        var ts = OccurredTimestamp.FromInstant(DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
        var largePng = RandomNumberGenerator.GetBytes(64 * 1024);

        await Assert.ThrowsAsync<StorageCapacityException>(() =>
            repo.AppendWithMediaBatchAsync(
            [
                new EventWithMediaWrite(
                    new ActivityEvent(StableId.NewEventId(ts.OccurredAtUtc), ts, SourceKind.ScreenFrame, "App", "Title"),
                    largePng,
                    ContentKind.ScreenshotPng)
            ], CancellationToken.None));

        // Lift quota and verify database integrity was preserved
        options.MaxStorageQuotaBytes = null;
        var health = await maintenance.CheckHealthAsync(CancellationToken.None);
        Assert.True(health.IsHealthy);
        Assert.Equal(0, health.EventCount);
    }

    [Fact]
    public async Task AbnormalRestart_RecoversRunningJob_AndPreservesCommittedEventAndMedia()
    {
        var options = new StorageOptions { DataDirectory = Path.Combine(_tempRoot, "case7-restart") };
        var keyStore = new StaticKeyStore(RandomNumberGenerator.GetBytes(32));
        var factory = new EncryptedSqliteConnectionFactory(options, keyStore);
        var clock = new SystemClock();
        var maintenance = new SqliteDatabaseMaintenanceService(factory);
        var mediaStore = new AesGcmMediaStore(factory, keyStore);
        var repository = new SqliteEventRepository(factory, mediaStore);
        var queue = new SqliteReportJobQueue(factory, clock);
        await maintenance.InitializeAsync(CancellationToken.None);

        var timestamp = ((IClock)clock).Now();
        var eventId = StableId.NewEventId(timestamp.OccurredAtUtc);
        var plaintext = Encoding.UTF8.GetBytes("encrypted media surviving restart");
        await repository.AppendWithMediaBatchAsync(
        [
            new EventWithMediaWrite(
                new ActivityEvent(eventId, timestamp, SourceKind.ScreenFrame, "Code", "Before restart"),
                plaintext,
                ContentKind.ScreenshotPng)
        ], CancellationToken.None);
        var enqueued = await queue.EnqueueAsync(
            new ReportJobRequest(timestamp.LocalDate, ReportTriggerSource.Manual),
            CancellationToken.None);
        var running = await queue.ClaimNextPendingAsync(CancellationToken.None);
        Assert.NotNull(running);
        Assert.Equal(ReportJobStatus.Running, running.Status);

        // All operation-scoped connections are closed. Rebuild the object graph as a new process would.
        var restartedFactory = new EncryptedSqliteConnectionFactory(options, keyStore);
        var restartedMaintenance = new SqliteDatabaseMaintenanceService(restartedFactory);
        var restartedMedia = new AesGcmMediaStore(restartedFactory, keyStore);
        var restartedRepository = new SqliteEventRepository(restartedFactory, restartedMedia);
        var restartedQueue = new SqliteReportJobQueue(restartedFactory, clock);
        await restartedMaintenance.InitializeAsync(CancellationToken.None);

        Assert.Equal(1, await restartedQueue.RecoverInterruptedJobsAsync(CancellationToken.None));
        var jobs = await restartedQueue.ListRecentJobsAsync(10, CancellationToken.None);
        Assert.Equal(ReportJobStatus.Pending, Assert.Single(jobs).Status);
        var restoredEvent = await restartedRepository.GetByIdAsync(eventId, CancellationToken.None);
        Assert.NotNull(restoredEvent?.MediaRef);
        var restoredMedia = await restartedMedia.ReadDecryptedAsync(restoredEvent.MediaRef.ContentId, CancellationToken.None);
        Assert.Equal(plaintext, restoredMedia);
        Assert.True((await restartedMaintenance.CheckHealthAsync(CancellationToken.None)).IsHealthy);
        Assert.Equal(enqueued.JobId, running.Id);
    }
}
