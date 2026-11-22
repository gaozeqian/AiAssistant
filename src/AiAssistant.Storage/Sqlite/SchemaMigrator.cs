using AiAssistant.Core.Exceptions;
using AiAssistant.Core.ValueObjects;
using Microsoft.Data.Sqlite;

namespace AiAssistant.Storage.Sqlite;

/// <summary>
/// Sequential forward-only schema migration runner. Refuses to run old application code against a newer schema (implementation.md §3.4).
/// </summary>
public static class SchemaMigrator
{
    private sealed record MigrationStep(SchemaVersion Version, string Sql);

    private static readonly MigrationStep[] Migrations =
    [
        new(
            new SchemaVersion(1, "S1_InitialEncryptedCoreSchema"),
            """
            CREATE TABLE IF NOT EXISTS content_refs (
                content_id TEXT PRIMARY KEY,
                kind INTEGER NOT NULL,
                sha256_hex TEXT NOT NULL,
                plaintext_length INTEGER NOT NULL,
                relative_path TEXT NOT NULL,
                ref_count INTEGER NOT NULL,
                phash TEXT NULL,
                created_at_utc TEXT NOT NULL,
                expires_at_utc TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS events (
                id TEXT PRIMARY KEY,
                occurred_at_utc TEXT NOT NULL,
                ended_at_utc TEXT NULL,
                local_date TEXT NOT NULL,
                time_zone_id TEXT NOT NULL,
                utc_offset_minutes INTEGER NOT NULL,
                source INTEGER NOT NULL,
                app_name TEXT NOT NULL,
                window_title TEXT NOT NULL,
                extracted_text TEXT NULL,
                summary TEXT NULL,
                content_id TEXT NULL REFERENCES content_refs(content_id),
                phash TEXT NULL,
                privacy INTEGER NOT NULL,
                metadata_json TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_events_occurred_at_utc ON events(occurred_at_utc);
            CREATE INDEX IF NOT EXISTS ix_events_local_date_utc ON events(local_date, occurred_at_utc);

            CREATE TABLE IF NOT EXISTS daily_reports (
                id TEXT PRIMARY KEY,
                local_date TEXT NOT NULL,
                version INTEGER NOT NULL,
                markdown TEXT NOT NULL,
                stats_json TEXT NOT NULL,
                model_id TEXT NOT NULL,
                endpoint_tier INTEGER NOT NULL,
                redaction_applied INTEGER NOT NULL,
                trigger_source INTEGER NOT NULL,
                generated_at_utc TEXT NOT NULL,
                UNIQUE(local_date, version)
            );

            CREATE TABLE IF NOT EXISTS report_jobs (
                id TEXT PRIMARY KEY,
                target_date TEXT NOT NULL,
                trigger_source INTEGER NOT NULL,
                force_overwrite INTEGER NOT NULL,
                status INTEGER NOT NULL,
                attempts INTEGER NOT NULL,
                last_error TEXT NULL,
                created_at_utc TEXT NOT NULL,
                next_attempt_at_utc TEXT NULL,
                finished_at_utc TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_report_jobs_status_next ON report_jobs(status, next_attempt_at_utc);

            CREATE TABLE IF NOT EXISTS voice_commands (
                id TEXT PRIMARY KEY,
                occurred_at_utc TEXT NOT NULL,
                wake_word TEXT NOT NULL,
                transcript TEXT NOT NULL,
                intent TEXT NOT NULL,
                params_json TEXT NOT NULL,
                endpoint_tier INTEGER NOT NULL,
                response_ref TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS vector_index_meta (
                index_name TEXT PRIMARY KEY,
                model_id TEXT NOT NULL,
                model_version TEXT NOT NULL,
                dimensions INTEGER NOT NULL,
                backend TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS event_vectors (
                event_id TEXT PRIMARY KEY REFERENCES events(id) ON DELETE CASCADE,
                embedding_blob BLOB NOT NULL,
                norm REAL NOT NULL
            );
            """)
        ,
        new(
            new SchemaVersion(2, "S7_VoiceCommandAudioOptIn"),
            """
            ALTER TABLE voice_commands ADD COLUMN audio_content_id TEXT NULL;
            CREATE INDEX IF NOT EXISTS ix_voice_commands_occurred ON voice_commands(occurred_at_utc);
            """)
    ];

    public static async Task<SchemaVersion> MigrateAsync(SqliteConnection connection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using (var bootCmd = connection.CreateCommand())
        {
            bootCmd.CommandText = """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version INTEGER PRIMARY KEY,
                    name TEXT NOT NULL,
                    applied_at_utc TEXT NOT NULL
                );
                """;
            await bootCmd.ExecuteNonQueryAsync(ct);
        }

        var current = await GetCurrentVersionAsync(connection, ct);
        if (current > SchemaVersion.Current)
        {
            throw new SchemaMigrationException(
                $"Database schema version ({current.Number}: {current.Name}) is newer than application supported version ({SchemaVersion.Current.Number}). Refusing to write with older application version.");
        }

        foreach (var step in Migrations.Where(m => m.Version.Number > current.Number).OrderBy(m => m.Version.Number))
        {
            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
            try
            {
                await using (var stepCmd = connection.CreateCommand())
                {
                    stepCmd.Transaction = tx;
                    stepCmd.CommandText = step.Sql;
                    await stepCmd.ExecuteNonQueryAsync(ct);
                }

                await using (var recordCmd = connection.CreateCommand())
                {
                    recordCmd.Transaction = tx;
                    recordCmd.CommandText = """
                        INSERT INTO schema_migrations(version, name, applied_at_utc)
                        VALUES ($version, $name, $appliedAtUtc);
                        """;
                    recordCmd.Parameters.AddWithValue("$version", step.Version.Number);
                    recordCmd.Parameters.AddWithValue("$name", step.Version.Name);
                    recordCmd.Parameters.AddWithValue("$appliedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
                    await recordCmd.ExecuteNonQueryAsync(ct);
                }

                await tx.CommitAsync(ct);
                current = step.Version;
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(ct);
                throw new SchemaMigrationException(
                    $"Failed applying migration {step.Version.Number} ({step.Version.Name}). Database rolled back cleanly.",
                    ex);
            }
        }

        return current;
    }

    public static async Task<SchemaVersion> GetCurrentVersionAsync(SqliteConnection connection, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT version, name
            FROM schema_migrations
            ORDER BY version DESC
            LIMIT 1;
            """;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return SchemaVersion.Empty;
        }

        return new SchemaVersion(reader.GetInt32(0), reader.GetString(1));
    }
}
