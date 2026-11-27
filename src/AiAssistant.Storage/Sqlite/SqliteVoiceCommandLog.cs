using System.Globalization;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Storage.Sqlite;

namespace AiAssistant.Storage.Sqlite;

/// <summary>
/// Encrypted voice command log. Text and the optional audio reference live here; the audio bytes
/// themselves stay in the encrypted media store so retention sweeps can erase them independently.
/// </summary>
public sealed class SqliteVoiceCommandLog(EncryptedSqliteConnectionFactory connectionFactory) : IVoiceCommandLog
{
    public async Task AppendAsync(VoiceCommandLogRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var conn = await connectionFactory.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO voice_commands (
                id, occurred_at_utc, wake_word, transcript, intent, params_json,
                endpoint_tier, response_ref, audio_content_id
            ) VALUES (
                $id, $occurredAt, $wakeWord, $transcript, $intent, $params,
                $tier, $responseRef, $audioContentId
            );
            """;
        cmd.Parameters.AddWithValue("$id", record.Id);
        cmd.Parameters.AddWithValue("$occurredAt", record.OccurredAtUtc.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("$wakeWord", record.WakeWord);
        cmd.Parameters.AddWithValue("$transcript", record.Transcript);
        cmd.Parameters.AddWithValue("$intent", record.Intent);
        cmd.Parameters.AddWithValue("$params", record.ParamsJson);
        cmd.Parameters.AddWithValue("$tier", (int)record.EndpointTier);
        cmd.Parameters.AddWithValue("$responseRef", (object?)record.ResponseRef ?? DBNull.Value);
        // The schema stores audio references separately from the text response reference.
        cmd.Parameters.AddWithValue("$audioContentId", (object?)record.AudioContentId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<VoiceCommandLogRecord>> ListRecentAsync(int limit, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var records = new List<VoiceCommandLogRecord>();
        await using var conn = await connectionFactory.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, occurred_at_utc, wake_word, transcript, intent, params_json,
                   endpoint_tier, response_ref, audio_content_id
            FROM voice_commands
            ORDER BY occurred_at_utc DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            records.Add(new VoiceCommandLogRecord(
                reader.GetString(0),
                DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                (ModelEndpointTier)reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return records;
    }
}
