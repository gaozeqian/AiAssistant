using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Core.Exceptions;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Storage.Media;
using Microsoft.Data.Sqlite;

namespace AiAssistant.Storage.Sqlite;

/// <summary>
/// Transactional SQLite3MC event repository ensuring events and AES-GCM media blobs become visible atomically.
/// </summary>
public sealed class SqliteEventRepository : IEventRepository
{
    private readonly EncryptedSqliteConnectionFactory _connectionFactory;
    private readonly AesGcmMediaStore _mediaStore;

    public SqliteEventRepository(EncryptedSqliteConnectionFactory connectionFactory, AesGcmMediaStore mediaStore)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(mediaStore);
        _connectionFactory = connectionFactory;
        _mediaStore = mediaStore;
    }

    public Task AppendBatchAsync(IReadOnlyList<ActivityEvent> events, CancellationToken ct)
    {
        var items = events.Select(e => new EventWithMediaWrite(e)).ToArray();
        return AppendWithMediaBatchAsync(items, ct);
    }

    public async Task AppendWithMediaBatchAsync(IReadOnlyList<EventWithMediaWrite> items, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return;
        }

        var stagedBlobs = new List<AesGcmMediaStore.StagedEncryptedBlob>();
        var finalEvents = new List<(ActivityEvent Event, AesGcmMediaStore.StagedEncryptedBlob? Blob)>(items.Count);

        try
        {
            foreach (var item in items)
            {
                AesGcmMediaStore.StagedEncryptedBlob? staged = null;
                var ev = item.Event;

                if (item.RawMediaBytes is { Length: > 0 } rawBytes)
                {
                    var contentId = ev.MediaRef?.ContentId ?? StableId.NewContentId(ev.Timestamp.OccurredAtUtc);
                    var retention = item.MediaRetention ?? _connectionFactory.Options.DefaultMediaRetention;
                    var writeReq = new ContentWriteRequest(
                        contentId,
                        item.MediaKind ?? ContentKind.ScreenshotPng,
                        rawBytes,
                        ev.Timestamp.OccurredAtUtc,
                        ev.Timestamp.OccurredAtUtc.Add(retention),
                        ev.PerceptualHash);

                    staged = await _mediaStore.StageEncryptedBlobAsync(writeReq, ct);
                    stagedBlobs.Add(staged);
                    ev = ev with { MediaRef = staged.Reference };
                }

                finalEvents.Add((ev, staged));
            }

            _connectionFactory.EnsureQuotaAllowsWrite(items.Count * 1024L);

            await using var conn = await _connectionFactory.OpenConnectionAsync(ct);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
            try
            {
                foreach (var (ev, blob) in finalEvents)
                {
                    if (blob is not null)
                    {
                        await AesGcmMediaStore.UpsertContentRefAsync(conn, tx, blob, ct);
                    }

                    await InsertEventRowAsync(conn, tx, ev, ct);
                }

                _connectionFactory.Options.FaultBeforeEventTransactionCommit?.Invoke();
                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        catch (Exception ex)
        {
            foreach (var staged in stagedBlobs)
            {
                AesGcmMediaStore.DeletePhysicalBlobQuietly(staged.FullPath);
            }

            throw EncryptedSqliteConnectionFactory.TranslateSqliteException(ex, _connectionFactory.Options.DatabasePath);
        }
    }

    public async IAsyncEnumerable<ActivityEvent> QueryAsync(
        EventQuery query,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var conn = await _connectionFactory.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();

        var clauses = new List<string>();
        if (query.LocalDate.HasValue)
        {
            clauses.Add("e.local_date = $localDate");
            cmd.Parameters.AddWithValue("$localDate", query.LocalDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        if (query.FromUtc.HasValue)
        {
            clauses.Add("e.occurred_at_utc >= $fromUtc");
            cmd.Parameters.AddWithValue("$fromUtc", query.FromUtc.Value.ToUniversalTime().ToString("O"));
        }

        if (query.ToUtc.HasValue)
        {
            clauses.Add("e.occurred_at_utc <= $toUtc");
            cmd.Parameters.AddWithValue("$toUtc", query.ToUtc.Value.ToUniversalTime().ToString("O"));
        }

        if (query.Source.HasValue)
        {
            clauses.Add("e.source = $source");
            cmd.Parameters.AddWithValue("$source", (int)query.Source.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.SearchKeyword))
        {
            clauses.Add("(e.app_name LIKE $kw OR e.window_title LIKE $kw OR e.extracted_text LIKE $kw OR e.summary LIKE $kw)");
            cmd.Parameters.AddWithValue("$kw", $"%{query.SearchKeyword.Trim()}%");
        }

        var whereSql = clauses.Count > 0 ? $"WHERE {string.Join(" AND ", clauses)}" : string.Empty;
        var limit = Math.Clamp(query.Limit, 1, 10_000);
        cmd.Parameters.AddWithValue("$limit", limit);

        cmd.CommandText = $"""
            SELECT
                e.id, e.occurred_at_utc, e.ended_at_utc, e.local_date, e.time_zone_id, e.utc_offset_minutes,
                e.source, e.app_name, e.window_title, e.extracted_text, e.summary, e.phash, e.privacy, e.metadata_json,
                c.content_id, c.kind, c.sha256_hex, c.plaintext_length, c.created_at_utc, c.expires_at_utc, c.phash
            FROM events e
            LEFT JOIN content_refs c ON e.content_id = c.content_id
            {whereSql}
            ORDER BY e.occurred_at_utc ASC
            LIMIT $limit;
            """;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            yield return ReadEvent(reader);
        }
    }

    public async Task<ActivityEvent?> GetByIdAsync(string eventId, CancellationToken ct)
    {
        await using var conn = await _connectionFactory.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                e.id, e.occurred_at_utc, e.ended_at_utc, e.local_date, e.time_zone_id, e.utc_offset_minutes,
                e.source, e.app_name, e.window_title, e.extracted_text, e.summary, e.phash, e.privacy, e.metadata_json,
                c.content_id, c.kind, c.sha256_hex, c.plaintext_length, c.created_at_utc, c.expires_at_utc, c.phash
            FROM events e
            LEFT JOIN content_refs c ON e.content_id = c.content_id
            WHERE e.id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", eventId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadEvent(reader) : null;
    }

    public async Task<IReadOnlyList<DateOnly>> ListActiveDatesAsync(CancellationToken ct)
    {
        var dates = new List<DateOnly>();
        await using var conn = await _connectionFactory.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT local_date FROM events ORDER BY local_date DESC;";

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            dates.Add(DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        return dates;
    }

    public async Task<bool> DeleteEventAsync(string eventId, CancellationToken ct)
    {
        await using var conn = await _connectionFactory.OpenConnectionAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        string? contentId = null;
        await using (var findCmd = conn.CreateCommand())
        {
            findCmd.Transaction = tx;
            findCmd.CommandText = "SELECT content_id FROM events WHERE id = $id;";
            findCmd.Parameters.AddWithValue("$id", eventId);
            var scalar = await findCmd.ExecuteScalarAsync(ct);
            if (scalar is null)
            {
                return false;
            }

            contentId = scalar as string;
        }

        await using (var delCmd = conn.CreateCommand())
        {
            delCmd.Transaction = tx;
            delCmd.CommandText = "DELETE FROM events WHERE id = $id;";
            delCmd.Parameters.AddWithValue("$id", eventId);
            await delCmd.ExecuteNonQueryAsync(ct);
        }

        if (!string.IsNullOrEmpty(contentId))
        {
            await _mediaStore.DecrementRefInTransactionAsync(conn, tx, contentId, ct);
        }

        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<int> PurgeDateAsync(DateOnly localDate, CancellationToken ct)
    {
        var dateStr = localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await using var conn = await _connectionFactory.OpenConnectionAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        var contentIds = new List<string>();
        await using (var findCmd = conn.CreateCommand())
        {
            findCmd.Transaction = tx;
            findCmd.CommandText = "SELECT content_id FROM events WHERE local_date = $date AND content_id IS NOT NULL;";
            findCmd.Parameters.AddWithValue("$date", dateStr);
            await using var reader = await findCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                contentIds.Add(reader.GetString(0));
            }
        }

        int deleted;
        await using (var delCmd = conn.CreateCommand())
        {
            delCmd.Transaction = tx;
            delCmd.CommandText = "DELETE FROM events WHERE local_date = $date;";
            delCmd.Parameters.AddWithValue("$date", dateStr);
            deleted = await delCmd.ExecuteNonQueryAsync(ct);
        }

        foreach (var contentId in contentIds)
        {
            await _mediaStore.DecrementRefInTransactionAsync(conn, tx, contentId, ct);
        }

        await tx.CommitAsync(ct);
        return deleted;
    }

    private static async Task InsertEventRowAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        ActivityEvent ev,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO events (
                id, occurred_at_utc, ended_at_utc, local_date, time_zone_id, utc_offset_minutes,
                source, app_name, window_title, extracted_text, summary, content_id, phash, privacy, metadata_json
            ) VALUES (
                $id, $utc, $endedUtc, $localDate, $tzId, $offsetMin,
                $source, $app, $title, $text, $summary, $contentId, $phash, $privacy, $meta
            );
            """;
        cmd.Parameters.AddWithValue("$id", ev.Id);
        cmd.Parameters.AddWithValue("$utc", ev.Timestamp.OccurredAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$endedUtc", ev.EndedAtUtc.HasValue ? ev.EndedAtUtc.Value.ToString("O") : DBNull.Value);
        cmd.Parameters.AddWithValue("$localDate", ev.Timestamp.LocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$tzId", ev.Timestamp.TimeZoneId);
        cmd.Parameters.AddWithValue("$offsetMin", ev.Timestamp.UtcOffsetMinutes);
        cmd.Parameters.AddWithValue("$source", (int)ev.Source);
        cmd.Parameters.AddWithValue("$app", ev.AppName);
        cmd.Parameters.AddWithValue("$title", ev.WindowTitle);
        cmd.Parameters.AddWithValue("$text", (object?)ev.ExtractedText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$summary", (object?)ev.Summary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$contentId", (object?)ev.MediaRef?.ContentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$phash", ev.PerceptualHash.HasValue
            ? ev.PerceptualHash.Value.ToString("x16", CultureInfo.InvariantCulture)
            : DBNull.Value);
        cmd.Parameters.AddWithValue("$privacy", (int)ev.Privacy);
        cmd.Parameters.AddWithValue("$meta", JsonSerializer.Serialize(ev.Metadata));

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static ActivityEvent ReadEvent(SqliteDataReader reader)
    {
        var id = reader.GetString(0);
        var occurredUtc = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var endedUtc = reader.IsDBNull(2)
            ? (DateTimeOffset?)null
            : DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var localDate = DateOnly.ParseExact(reader.GetString(3), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var tzId = reader.GetString(4);
        var offsetMin = reader.GetInt32(5);
        var source = (SourceKind)reader.GetInt32(6);
        var appName = reader.GetString(7);
        var windowTitle = reader.GetString(8);
        var extractedText = reader.IsDBNull(9) ? null : reader.GetString(9);
        var summary = reader.IsDBNull(10) ? null : reader.GetString(10);
        var phash = reader.IsDBNull(11) ? (ulong?)null : ulong.Parse(reader.GetString(11), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var privacy = (PrivacyLevel)reader.GetInt32(12);
        var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(13))
            ?? new Dictionary<string, string>(StringComparer.Ordinal);

        ContentRef? contentRef = null;
        if (!reader.IsDBNull(14))
        {
            var cId = reader.GetString(14);
            var cKind = (ContentKind)reader.GetInt32(15);
            var cSha = reader.GetString(16);
            var cLen = reader.GetInt64(17);
            var cCreated = DateTimeOffset.Parse(reader.GetString(18), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            var cExpires = reader.IsDBNull(19)
                ? (DateTimeOffset?)null
                : DateTimeOffset.Parse(reader.GetString(19), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            var cPhash = reader.IsDBNull(20)
                ? (ulong?)null
                : ulong.Parse(reader.GetString(20), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

            contentRef = new ContentRef(cId, cKind, cSha, cLen, cCreated, cExpires, cPhash);
        }

        return new ActivityEvent(
            id,
            OccurredTimestamp.FromStored(occurredUtc, localDate, tzId, offsetMin),
            source,
            appName,
            windowTitle,
            extractedText,
            summary,
            contentRef,
            phash,
            privacy,
            endedUtc,
            metadata);
    }
}

/// <summary>
/// Persistent idempotent report job queue supporting crash recovery (ADR-0010).
/// </summary>
public sealed class SqliteReportJobQueue(EncryptedSqliteConnectionFactory connectionFactory, IClock clock) : IReportJobQueue
{
    public async Task<EnqueueResult> EnqueueAsync(ReportJobRequest request, CancellationToken ct)
    {
        var dateStr = request.TargetDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await using var conn = await connectionFactory.OpenConnectionAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        if (!request.ForceOverwrite)
        {
            await using var existingCmd = conn.CreateCommand();
            existingCmd.Transaction = tx;
            existingCmd.CommandText = """
                SELECT id FROM report_jobs
                WHERE target_date = $date AND status IN (0, 1)
                LIMIT 1;
                """;
            existingCmd.Parameters.AddWithValue("$date", dateStr);
            if (await existingCmd.ExecuteScalarAsync(ct) is string existingId)
            {
                await tx.CommitAsync(ct);
                return new EnqueueResult(existingId, DeduplicatedExistingPendingJob: true);
            }
        }

        var now = clock.UtcNow;
        var jobId = StableId.NewJobId(now);
        await using (var insCmd = conn.CreateCommand())
        {
            insCmd.Transaction = tx;
            insCmd.CommandText = """
                INSERT INTO report_jobs (
                    id, target_date, trigger_source, force_overwrite, status, attempts, last_error, created_at_utc, next_attempt_at_utc, finished_at_utc
                ) VALUES (
                    $id, $date, $source, $force, 0, 0, NULL, $now, $now, NULL
                );
                """;
            insCmd.Parameters.AddWithValue("$id", jobId);
            insCmd.Parameters.AddWithValue("$date", dateStr);
            insCmd.Parameters.AddWithValue("$source", (int)request.TriggerSource);
            insCmd.Parameters.AddWithValue("$force", request.ForceOverwrite ? 1 : 0);
            insCmd.Parameters.AddWithValue("$now", now.ToUniversalTime().ToString("O"));
            await insCmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return new EnqueueResult(jobId, DeduplicatedExistingPendingJob: false);
    }

    public async Task<int> RecoverInterruptedJobsAsync(CancellationToken ct)
    {
        await using var conn = await connectionFactory.OpenConnectionAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        var runningJobs = new List<ReportJob>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT id, target_date, trigger_source, force_overwrite, status, attempts, last_error, created_at_utc, next_attempt_at_utc, finished_at_utc
                FROM report_jobs
                WHERE status = 1;
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                runningJobs.Add(ReadJob(reader));
            }
        }

        var now = clock.UtcNow;
        foreach (var job in runningJobs)
        {
            job.RecoverInterrupted(now);
            await UpdateInTransactionAsync(conn, tx, job, ct);
        }

        await tx.CommitAsync(ct);
        return runningJobs.Count;
    }

    public async Task<ReportJob?> ClaimNextPendingAsync(CancellationToken ct)
    {
        var now = clock.UtcNow.ToUniversalTime();
        await using var conn = await connectionFactory.OpenConnectionAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        ReportJob? next = null;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT id, target_date, trigger_source, force_overwrite, status, attempts, last_error, created_at_utc, next_attempt_at_utc, finished_at_utc
                FROM report_jobs
                WHERE status = 0 AND (next_attempt_at_utc IS NULL OR next_attempt_at_utc <= $now)
                ORDER BY created_at_utc ASC
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$now", now.ToString("O"));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                next = ReadJob(reader);
            }
        }

        if (next is null)
        {
            await tx.CommitAsync(ct);
            return null;
        }

        next.MarkRunning(now);
        await UpdateInTransactionAsync(conn, tx, next, ct);
        await tx.CommitAsync(ct);
        return next;
    }

    public async Task UpdateAsync(ReportJob job, CancellationToken ct)
    {
        await using var conn = await connectionFactory.OpenConnectionAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        await UpdateInTransactionAsync(conn, tx, job, ct);
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<ReportJob>> ListRecentJobsAsync(int limit, CancellationToken ct)
    {
        var list = new List<ReportJob>();
        await using var conn = await connectionFactory.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, target_date, trigger_source, force_overwrite, status, attempts, last_error, created_at_utc, next_attempt_at_utc, finished_at_utc
            FROM report_jobs
            ORDER BY created_at_utc DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(ReadJob(reader));
        }

        return list;
    }

    private static async Task UpdateInTransactionAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        ReportJob job,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE report_jobs
            SET status = $status,
                attempts = $attempts,
                last_error = $error,
                next_attempt_at_utc = $nextAt,
                finished_at_utc = $finishedAt
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", job.Id);
        cmd.Parameters.AddWithValue("$status", (int)job.Status);
        cmd.Parameters.AddWithValue("$attempts", job.Attempts);
        cmd.Parameters.AddWithValue("$error", (object?)job.LastError ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$nextAt", job.NextAttemptAtUtc.HasValue ? job.NextAttemptAtUtc.Value.ToString("O") : DBNull.Value);
        cmd.Parameters.AddWithValue("$finishedAt", job.FinishedAtUtc.HasValue ? job.FinishedAtUtc.Value.ToString("O") : DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static ReportJob ReadJob(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            DateOnly.ParseExact(reader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            (ReportTriggerSource)reader.GetInt32(2),
            reader.GetInt32(3) != 0,
            DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            (ReportJobStatus)reader.GetInt32(4),
            reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
}

/// <summary>
/// Manages database bootstrap migrations, integrity checks, encrypted online backups, and safe restores.
/// </summary>
public sealed class SqliteDatabaseMaintenanceService(EncryptedSqliteConnectionFactory connectionFactory) : IDatabaseMaintenanceService
{
    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var conn = await connectionFactory.OpenConnectionAsync(ct);
        _ = await SchemaMigrator.MigrateAsync(conn, ct);
    }

    public async Task<DatabaseHealthReport> CheckHealthAsync(CancellationToken ct)
    {
        await using var conn = await connectionFactory.OpenConnectionAsync(ct);
        var version = await SchemaMigrator.GetCurrentVersionAsync(conn, ct);

        string integrity;
        await using (var integrityCmd = conn.CreateCommand())
        {
            integrityCmd.CommandText = "PRAGMA integrity_check;";
            integrity = (await integrityCmd.ExecuteScalarAsync(ct))?.ToString() ?? "unknown";
        }

        if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new StorageIntegrityException($"SQLite integrity_check failed: {integrity}");
        }

        long eventCount;
        await using (var evCmd = conn.CreateCommand())
        {
            evCmd.CommandText = "SELECT count(*) FROM events;";
            eventCount = Convert.ToInt64(await evCmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }

        long blobCount;
        await using (var blobCmd = conn.CreateCommand())
        {
            blobCmd.CommandText = "SELECT count(*) FROM content_refs;";
            blobCount = Convert.ToInt64(await blobCmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }

        return new DatabaseHealthReport(
            IsHealthy: true,
            SchemaVersion: version,
            CipherDescription: "SQLite3MC (AES-256 / ChaCha20-Poly1305)",
            VectorBackend: connectionFactory.VectorBackendDiagnostic,
            EventCount: eventCount,
            EncryptedBlobCount: blobCount,
            IntegrityMessage: integrity);
    }

    public async Task BackupEncryptedAsync(string destinationPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var parent = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        await using var sourceConn = await connectionFactory.OpenConnectionAsync(ct);
        await using var destConn = await connectionFactory.OpenConnectionForPathAsync(destinationPath, ct);
        sourceConn.BackupDatabase(destConn);
        await destConn.CloseAsync();
    }

    public async Task RestoreEncryptedAsync(string backupSourcePath, CancellationToken ct)
    {
        if (!File.Exists(backupSourcePath))
        {
            throw new FileNotFoundException("Encrypted backup file does not exist.", backupSourcePath);
        }

        // Verify the backup can be decrypted with current key and passes integrity_check BEFORE touching active DB!
        await using (var verifyConn = await connectionFactory.OpenConnectionForPathAsync(backupSourcePath, ct))
        {
            await using var cmd = verifyConn.CreateCommand();
            cmd.CommandText = "PRAGMA integrity_check;";
            var status = (await cmd.ExecuteScalarAsync(ct))?.ToString();
            if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new StorageIntegrityException($"Refusing to restore corrupted backup '{backupSourcePath}': {status}");
            }

            var backupVersion = await SchemaMigrator.GetCurrentVersionAsync(verifyConn, ct);
            if (backupVersion > SchemaVersion.Current)
            {
                throw new SchemaMigrationException(
                    $"Backup schema version ({backupVersion.Number}) is newer than current application ({SchemaVersion.Current.Number}).");
            }
        }

        var dbPath = connectionFactory.Options.DatabasePath;
        var walPath = $"{dbPath}-wal";
        var shmPath = $"{dbPath}-shm";
        if (File.Exists(walPath))
        {
            File.Delete(walPath);
        }

        if (File.Exists(shmPath))
        {
            File.Delete(shmPath);
        }

        File.Copy(backupSourcePath, dbPath, overwrite: true);

        // Apply forward migrations if the backup was from an older schema version
        await InitializeAsync(ct);
    }
}
