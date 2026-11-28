using System.Globalization;
using AiAssistant.Application.Ports;
using Microsoft.Data.Sqlite;

namespace AiAssistant.Storage.Sqlite;

/// <summary>
/// User-triggered deletion of captured data. Deletes rows and encrypted media blobs only; the OS key store,
/// models.json and sanitized logs are intentionally out of scope and documented as separate actions.
/// </summary>
public sealed class DataDeletionService : IDataDeletionService
{
    private readonly EncryptedSqliteConnectionFactory _connectionFactory;
    private readonly IEventRepository _events;

    public DataDeletionService(EncryptedSqliteConnectionFactory connectionFactory, IEventRepository events)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(events);
        _connectionFactory = connectionFactory;
        _events = events;
    }

    /// <summary>
    /// Microsoft.Data.Sqlite's async APIs run synchronously and the media sweep is plain file IO, so
    /// the whole purge would otherwise execute on the caller's thread and freeze the desktop UI.
    /// The core is dispatched to the thread pool, exactly like the backup/restore paths.
    /// </summary>
    public Task<DataDeletionResult> PurgeDateAsync(DateOnly localDate, CancellationToken ct) =>
        Task.Run(() => PurgeDateCoreAsync(localDate, ct), ct);

    private async Task<DataDeletionResult> PurgeDateCoreAsync(DateOnly localDate, CancellationToken ct)
    {
        var date = localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var eventsDeleted = await _events.PurgeDateAsync(localDate, ct);

        await using var connection = await _connectionFactory.OpenConnectionAsync(ct);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        var reports = await ExecuteAsync(connection, tx, "DELETE FROM daily_reports WHERE local_date = $date;", date, ct);
        // 周报跨越 7 天：只要区间覆盖该日期，就说明它包含这一天的数据，必须一并删除。
        reports += await ExecuteAsync(
            connection,
            tx,
            "DELETE FROM weekly_reports WHERE week_start <= $date AND week_end >= $date;",
            date,
            ct);
        var jobs = await ExecuteAsync(connection, tx, "DELETE FROM report_jobs WHERE target_date = $date;", date, ct);
        await tx.CommitAsync(ct);
        await CheckpointAsync(connection, ct);

        return new DataDeletionResult(eventsDeleted, reports, jobs, 0, 0);
    }

    public Task<DataDeletionResult> PurgeAllAsync(CancellationToken ct) =>
        Task.Run(() => PurgeAllCoreAsync(ct), ct);

    private async Task<DataDeletionResult> PurgeAllCoreAsync(CancellationToken ct)
    {
        int events;
        int reports;
        int jobs;
        int voiceCommands;
        int mediaRefs;
        await using (var connection = await _connectionFactory.OpenConnectionAsync(ct))
        {
            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
            events = await CountAsync(connection, tx, "SELECT COUNT(*) FROM events;", ct);
            reports = await CountAsync(connection, tx, "SELECT COUNT(*) FROM daily_reports;", ct);
            reports += await CountAsync(connection, tx, "SELECT COUNT(*) FROM weekly_reports;", ct);
            jobs = await CountAsync(connection, tx, "SELECT COUNT(*) FROM report_jobs;", ct);
            voiceCommands = await CountAsync(connection, tx, "SELECT COUNT(*) FROM voice_commands;", ct);
            mediaRefs = await CountAsync(connection, tx, "SELECT COUNT(*) FROM content_refs;", ct);

            await ExecuteAsync(connection, tx, "DELETE FROM event_vectors;", null, ct);
            await ExecuteAsync(connection, tx, "DELETE FROM events;", null, ct);
            await ExecuteAsync(connection, tx, "DELETE FROM content_refs;", null, ct);
            await ExecuteAsync(connection, tx, "DELETE FROM daily_reports;", null, ct);
            await ExecuteAsync(connection, tx, "DELETE FROM weekly_reports;", null, ct);
            await ExecuteAsync(connection, tx, "DELETE FROM report_jobs;", null, ct);
            await ExecuteAsync(connection, tx, "DELETE FROM voice_commands;", null, ct);
            await ExecuteAsync(connection, tx, "DELETE FROM vector_index_meta;", null, ct);
            await tx.CommitAsync(ct);
            await CheckpointAsync(connection, ct);
        }

        var mediaFiles = DeleteMediaFiles();
        return new DataDeletionResult(events, reports, jobs, voiceCommands, Math.Max(mediaRefs, mediaFiles));
    }

    private int DeleteMediaFiles()
    {
        var directory = _connectionFactory.Options.MediaDirectory;
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*.enc", SearchOption.AllDirectories))
        {
            try
            {
                File.Delete(file);
                deleted++;
            }
            catch (IOException ex)
            {
                // A reader may still hold the blob; the row is already gone so it can never be referenced again.
                AiAssistant.Application.Diagnostics.SuppressedExceptionLog.Trace("Storage.Sqlite.DataDeletionService.DeleteMediaFiles.IO", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                // 预期降级：无权限删除该文件时跳过，相关数据库记录已清除不会再被引用。
                AiAssistant.Application.Diagnostics.SuppressedExceptionLog.Trace("Storage.Sqlite.DataDeletionService.DeleteMediaFiles.AccessDenied", ex);
            }
        }

        return deleted;
    }

    private static async Task CheckpointAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int> CountAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string sql,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync(ct);
        return result is null or DBNull ? 0 : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static async Task<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string sql,
        string? date,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        if (date is not null)
        {
            command.Parameters.AddWithValue("$date", date);
        }

        return await command.ExecuteNonQueryAsync(ct);
    }
}
