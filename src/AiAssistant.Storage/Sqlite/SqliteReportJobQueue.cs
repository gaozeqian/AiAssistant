using System.Globalization;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;
using Microsoft.Data.Sqlite;

namespace AiAssistant.Storage.Sqlite;

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
