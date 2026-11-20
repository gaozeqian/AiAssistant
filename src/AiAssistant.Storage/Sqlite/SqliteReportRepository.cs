using System.Globalization;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;
using Microsoft.Data.Sqlite;

namespace AiAssistant.Storage.Sqlite;

/// <summary>Versioned encrypted daily report repository; each append atomically allocates the next version.</summary>
public sealed class SqliteReportRepository(EncryptedSqliteConnectionFactory connectionFactory) : IReportRepository
{
    public async Task<DailyReport?> GetLatestAsync(DateOnly localDate, CancellationToken ct)
    {
        await using var conn = await connectionFactory.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {Columns}
            FROM daily_reports
            WHERE local_date = $date
            ORDER BY version DESC
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$date", localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<DailyReport>> ListVersionsAsync(DateOnly localDate, CancellationToken ct)
    {
        var reports = new List<DailyReport>();
        await using var conn = await connectionFactory.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {Columns}
            FROM daily_reports
            WHERE local_date = $date
            ORDER BY version DESC
            LIMIT 100;
            """;
        cmd.Parameters.AddWithValue("$date", localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            reports.Add(Read(reader));
        }

        return reports;
    }

    public async Task<DailyReport> AppendAsync(
        DateOnly localDate,
        string markdown,
        string statsJson,
        string modelId,
        ModelEndpointTier endpointTier,
        bool redactionApplied,
        ReportTriggerSource triggerSource,
        DateTimeOffset generatedAtUtc,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markdown);
        ArgumentException.ThrowIfNullOrWhiteSpace(statsJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        await using var conn = await connectionFactory.OpenConnectionAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        int nextVersion;
        await using (var versionCmd = conn.CreateCommand())
        {
            versionCmd.Transaction = tx;
            versionCmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM daily_reports WHERE local_date = $date;";
            versionCmd.Parameters.AddWithValue("$date", localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            nextVersion = Convert.ToInt32(await versionCmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) + 1;
        }

        var id = StableId.NewReportId(generatedAtUtc);
        await using (var insertCmd = conn.CreateCommand())
        {
            insertCmd.Transaction = tx;
            insertCmd.CommandText = """
                INSERT INTO daily_reports (
                    id, local_date, version, markdown, stats_json, model_id,
                    endpoint_tier, redaction_applied, trigger_source, generated_at_utc
                ) VALUES (
                    $id, $date, $version, $markdown, $stats, $model,
                    $tier, $redaction, $trigger, $generatedAt
                );
                """;
            insertCmd.Parameters.AddWithValue("$id", id);
            insertCmd.Parameters.AddWithValue("$date", localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            insertCmd.Parameters.AddWithValue("$version", nextVersion);
            insertCmd.Parameters.AddWithValue("$markdown", markdown);
            insertCmd.Parameters.AddWithValue("$stats", statsJson);
            insertCmd.Parameters.AddWithValue("$model", modelId);
            insertCmd.Parameters.AddWithValue("$tier", (int)endpointTier);
            insertCmd.Parameters.AddWithValue("$redaction", redactionApplied ? 1 : 0);
            insertCmd.Parameters.AddWithValue("$trigger", (int)triggerSource);
            insertCmd.Parameters.AddWithValue("$generatedAt", generatedAtUtc.ToUniversalTime().ToString("O"));
            await insertCmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return new DailyReport(
            id,
            localDate,
            nextVersion,
            markdown,
            statsJson,
            modelId,
            endpointTier,
            redactionApplied,
            triggerSource,
            generatedAtUtc.ToUniversalTime());
    }

    private const string Columns = """
        id, local_date, version, markdown, stats_json, model_id,
        endpoint_tier, redaction_applied, trigger_source, generated_at_utc
        """;

    private static DailyReport Read(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            DateOnly.ParseExact(reader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            reader.GetInt32(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            (ModelEndpointTier)reader.GetInt32(6),
            reader.GetInt32(7) != 0,
            (ReportTriggerSource)reader.GetInt32(8),
            DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
}
