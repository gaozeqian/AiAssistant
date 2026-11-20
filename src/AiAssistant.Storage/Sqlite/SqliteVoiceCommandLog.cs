using AiAssistant.Application.Ports;
using AiAssistant.Storage.Sqlite;

namespace AiAssistant.Storage.Sqlite;

/// <summary>
/// Encrypted voice command text log. Audio is never written here; command audio remains in memory only.
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
}
