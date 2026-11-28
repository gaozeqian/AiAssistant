using System.Globalization;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;
using Microsoft.Data.Sqlite;

namespace AiAssistant.Storage.Sqlite;

/// <summary>Versioned encrypted weekly report repository; each append atomically allocates the next version.</summary>
public sealed class SqliteWeeklyReportRepository(EncryptedSqliteConnectionFactory connectionFactory)
    : IWeeklyReportRepository
{
    public async Task<WeeklyReport?> GetLatestAsync(DateOnly weekStart, CancellationToken ct)
    {
        await using var conn = await connectionFactory.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {Columns}
            FROM weekly_reports
            WHERE week_start = $week
            ORDER BY version DESC
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$week", Format(weekStart));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<WeeklyReport>> ListVersionsAsync(DateOnly weekStart, CancellationToken ct)
    {
        var reports = new List<WeeklyReport>();
        await using var conn = await connectionFactory.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {Columns}
            FROM weekly_reports
            WHERE week_start = $week
            ORDER BY version DESC
            LIMIT 100;
            """;
        cmd.Parameters.AddWithValue("$week", Format(weekStart));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            reports.Add(Read(reader));
        }

        return reports;
    }

    public async Task<WeeklyReport> AppendAsync(
        DateOnly weekStart,
        DateOnly weekEnd,
        string markdown,
        string payloadJson,
        string modelId,
        ReportTriggerSource triggerSource,
        DateTimeOffset generatedAtUtc,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markdown);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        await using var conn = await connectionFactory.OpenConnectionAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        int nextVersion;
        await using (var versionCmd = conn.CreateCommand())
        {
            versionCmd.Transaction = tx;
            versionCmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM weekly_reports WHERE week_start = $week;";
            versionCmd.Parameters.AddWithValue("$week", Format(weekStart));
            nextVersion = Convert.ToInt32(await versionCmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) + 1;
        }

        var id = StableId.NewReportId(generatedAtUtc);
        await using (var insertCmd = conn.CreateCommand())
        {
            insertCmd.Transaction = tx;
            insertCmd.CommandText = """
                INSERT INTO weekly_reports (
                    id, week_start, week_end, version, markdown, payload_json,
                    model_id, trigger_source, generated_at_utc
                ) VALUES (
                    $id, $week, $end, $version, $markdown, $payload,
                    $model, $trigger, $generatedAt
                );
                """;
            insertCmd.Parameters.AddWithValue("$id", id);
            insertCmd.Parameters.AddWithValue("$week", Format(weekStart));
            insertCmd.Parameters.AddWithValue("$end", Format(weekEnd));
            insertCmd.Parameters.AddWithValue("$version", nextVersion);
            insertCmd.Parameters.AddWithValue("$markdown", markdown);
            insertCmd.Parameters.AddWithValue("$payload", payloadJson);
            insertCmd.Parameters.AddWithValue("$model", modelId);
            insertCmd.Parameters.AddWithValue("$trigger", (int)triggerSource);
            insertCmd.Parameters.AddWithValue("$generatedAt", generatedAtUtc.ToUniversalTime().ToString("O"));
            await insertCmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return new WeeklyReport(
            id,
            weekStart,
            weekEnd,
            nextVersion,
            markdown,
            payloadJson,
            modelId,
            triggerSource,
            generatedAtUtc.ToUniversalTime());
    }

    private const string Columns = """
        id, week_start, week_end, version, markdown, payload_json,
        model_id, trigger_source, generated_at_utc
        """;

    private static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static WeeklyReport Read(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            DateOnly.ParseExact(reader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateOnly.ParseExact(reader.GetString(2), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            reader.GetInt32(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            (ReportTriggerSource)reader.GetInt32(7),
            DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
}
