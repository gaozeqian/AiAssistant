using System.Security.Cryptography;
using System.Text;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Storage.KeyStores;
using AiAssistant.Storage.Media;
using AiAssistant.Storage.Sqlite;

namespace AiAssistant.Storage.Tests;

public sealed class VoiceCommandLogTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(AppContext.BaseDirectory, "test-temp", Guid.NewGuid().ToString("N"));

    public VoiceCommandLogTests() => Directory.CreateDirectory(_tempRoot);

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
            // Best-effort test cleanup
        }
    }

    [Fact]
    public async Task VoiceCommandLog_StoresTextInEncryptedDatabase_WithoutAudioReference()
    {
        var options = new StorageOptions { DataDirectory = Path.Combine(_tempRoot, "voice") };
        var key = RandomNumberGenerator.GetBytes(32);
        var keyStore = new StaticKeyStore(key);
        var factory = new EncryptedSqliteConnectionFactory(options, keyStore);
        var maintenance = new SqliteDatabaseMaintenanceService(factory);
        var log = new SqliteVoiceCommandLog(factory);
        await maintenance.InitializeAsync(CancellationToken.None);
        const string transcript = "VOICE_TRANSCRIPT_SECRET_7731";

        await log.AppendAsync(
            new VoiceCommandLogRecord(
                "cmd_1",
                DateTimeOffset.UtcNow,
                "你好时光",
                transcript,
                "GenerateReport",
                "{}",
                ModelEndpointTier.LocalOffline,
                null,
                null),
            CancellationToken.None);

        await using (var conn = await factory.OpenConnectionAsync(CancellationToken.None))
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT transcript, audio_content_id FROM voice_commands WHERE id = 'cmd_1';";
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(transcript, reader.GetString(0));
            Assert.True(reader.IsDBNull(1));
        }

        await using (var conn = await factory.OpenConnectionAsync(CancellationToken.None))
        await using (var checkpoint = conn.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await checkpoint.ExecuteNonQueryAsync();
        }

        var raw = await File.ReadAllBytesAsync(options.DatabasePath);
        Assert.DoesNotContain(transcript, Encoding.UTF8.GetString(raw), StringComparison.Ordinal);
    }
}
