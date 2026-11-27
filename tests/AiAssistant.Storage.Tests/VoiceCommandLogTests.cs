using System.Security.Cryptography;
using System.Text;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Storage.KeyStores;
using AiAssistant.Storage.Media;
using AiAssistant.Storage.Sqlite;
using AiAssistant.Testing.Common;

namespace AiAssistant.Storage.Tests;

[Trait("Category", "Unit")]
public sealed class VoiceCommandLogTests : IDisposable
{
    private readonly string _tempRoot = TempArtifactDirectory.CreatePath("storage");

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

    [Fact]
    public async Task VoiceCommandLog_ListsNewestFirstWithAudioReference()
    {
        var options = new StorageOptions { DataDirectory = Path.Combine(_tempRoot, "voice-list") };
        var key = RandomNumberGenerator.GetBytes(32);
        var keyStore = new StaticKeyStore(key);
        var factory = new EncryptedSqliteConnectionFactory(options, keyStore);
        var maintenance = new SqliteDatabaseMaintenanceService(factory);
        var log = new SqliteVoiceCommandLog(factory);
        await maintenance.InitializeAsync(CancellationToken.None);

        var start = new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 3; i++)
        {
            await log.AppendAsync(
                new VoiceCommandLogRecord(
                    $"cmd_{i}",
                    start.AddMinutes(i),
                    "你好时光",
                    $"命令 {i}",
                    i == 2 ? "AskQuestion" : "PauseCapture",
                    "{}",
                    ModelEndpointTier.LocalOffline,
                    null,
                    i == 2 ? "cnt_audio_2" : null),
                CancellationToken.None);
        }

        var recent = await log.ListRecentAsync(2, CancellationToken.None);

        Assert.Equal(2, recent.Count);
        Assert.Equal("cmd_2", recent[0].Id);
        Assert.Equal("cmd_1", recent[1].Id);
        Assert.Equal("cnt_audio_2", recent[0].AudioContentId);
        Assert.Null(recent[1].AudioContentId);
    }

    [Fact]
    public async Task CommandAudioReference_IsClearedOnExpiry_WhileTextLogRemains()
    {
        var options = new StorageOptions { DataDirectory = Path.Combine(_tempRoot, "voice-audio") };
        var key = RandomNumberGenerator.GetBytes(32);
        var keyStore = new StaticKeyStore(key);
        var factory = new EncryptedSqliteConnectionFactory(options, keyStore);
        var maintenance = new SqliteDatabaseMaintenanceService(factory);
        var media = new AesGcmMediaStore(factory, keyStore);
        var log = new SqliteVoiceCommandLog(factory);
        await maintenance.InitializeAsync(CancellationToken.None);

        var created = new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
        byte[] wav = [0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00, 0x57, 0x41, 0x56, 0x45];
        var reference = await media.PutAsync(
            new ContentWriteRequest(
                "cnt_voice_audio",
                ContentKind.AudioPcm16Wav,
                wav,
                created,
                created.AddDays(7)),
            CancellationToken.None);

        await log.AppendAsync(
            new VoiceCommandLogRecord(
                "cmd_audio",
                created,
                "你好时光",
                "暂停采集",
                "PauseCapture",
                "{}",
                ModelEndpointTier.LocalOffline,
                null,
                reference.ContentId),
            CancellationToken.None);

        Assert.Equal(wav, await media.ReadDecryptedAsync(reference.ContentId, CancellationToken.None));

        Assert.Equal(0, await media.PurgeExpiredAsync(created.AddDays(6), CancellationToken.None));
        Assert.Equal(1, await media.PurgeExpiredAsync(created.AddDays(8), CancellationToken.None));
        Assert.Null(await media.ReadDecryptedAsync(reference.ContentId, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(options.MediaDirectory, "*.enc", SearchOption.AllDirectories));

        await using var conn = await factory.OpenConnectionAsync(CancellationToken.None);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT transcript, audio_content_id FROM voice_commands WHERE id = 'cmd_audio';";
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("暂停采集", reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
    }
}
