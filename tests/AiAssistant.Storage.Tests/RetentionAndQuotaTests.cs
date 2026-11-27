using System.Security.Cryptography;
using AiAssistant.Application.Ports;
using AiAssistant.Application.UseCases;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Storage.KeyStores;
using AiAssistant.Storage.Media;
using AiAssistant.Storage.Sqlite;
using AiAssistant.Testing.Common;

namespace AiAssistant.Storage.Tests;

[Trait("Category", "Unit")]
public sealed class RetentionAndQuotaTests : IDisposable
{
    private readonly string _tempRoot = TempArtifactDirectory.CreatePath("retention");

    public RetentionAndQuotaTests() => Directory.CreateDirectory(_tempRoot);

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
    public async Task RetentionSettingsStore_PersistsClampedPolicy()
    {
        var directory = Path.Combine(_tempRoot, "settings");
        var store = new RetentionSettingsStore(directory);
        var applied = new List<RetentionSettings>();
        var useCase = new RetentionSettingsUseCase(store, new RecordingApplier(applied));

        var initial = useCase.ApplyCurrent();
        Assert.Equal(RetentionSettings.Default, initial);
        Assert.Single(applied);

        var saved = await useCase.SaveAsync(
            mediaRetentionDays: 0,
            commandAudioRetentionDays: 9999,
            maxStorageGigabytes: -3);

        Assert.Equal(RetentionSettings.MinRetentionDays, saved.MediaRetentionDays);
        Assert.Equal(RetentionSettings.MaxRetentionDays, saved.CommandAudioRetentionDays);
        Assert.Equal(0, saved.MaxStorageGigabytes);
        Assert.Null(saved.MaxStorageBytes);
        Assert.Equal(2, applied.Count);

        var reloaded = new RetentionSettingsStore(directory).Load();
        Assert.Equal(saved, reloaded);
        Assert.True(File.Exists(Path.Combine(directory, "retention.json")));
    }

    [Fact]
    public async Task MediaStore_PurgeOverQuota_ErasesOldestBlobsAndUnlinksReferences()
    {
        var options = new StorageOptions { DataDirectory = Path.Combine(_tempRoot, "quota") };
        var key = RandomNumberGenerator.GetBytes(32);
        var keyStore = new StaticKeyStore(key);
        var factory = new EncryptedSqliteConnectionFactory(options, keyStore);
        var maintenance = new SqliteDatabaseMaintenanceService(factory);
        var media = new AesGcmMediaStore(factory, keyStore);
        await maintenance.InitializeAsync(CancellationToken.None);

        var start = new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
        var payload = new byte[1000];
        Random.Shared.NextBytes(payload);
        var ids = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var created = start.AddHours(i);
            var reference = await media.PutAsync(
                new ContentWriteRequest(
                    $"cnt_quota_{i}",
                    ContentKind.ScreenshotPng,
                    payload,
                    created,
                    created.AddDays(30)),
                CancellationToken.None);
            ids.Add(reference.ContentId);
        }

        // Each blob occupies 32 bytes of header/nonce/tag plus the payload. The cap covers the whole
        // data directory (database included), exactly like the write gate.
        var perBlob = payload.Length + 32;
        var target = DirectorySize(options.DataDirectory) - (perBlob / 2);
        var purged = await media.PurgeOverQuotaAsync(target, CancellationToken.None);

        Assert.True(purged >= 1, "至少应淘汰一个媒体");
        Assert.True(DirectorySize(options.DataDirectory) <= target, "清理后应回到上限以内");
        Assert.Null(await media.ReadDecryptedAsync(ids[0], CancellationToken.None));
        Assert.Equal(3 - purged, Directory.GetFiles(options.MediaDirectory, "*.enc", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task MediaStore_PurgeOverQuota_IsNoOpWhenUnderLimit()
    {
        var options = new StorageOptions { DataDirectory = Path.Combine(_tempRoot, "quota-under") };
        var key = RandomNumberGenerator.GetBytes(32);
        var keyStore = new StaticKeyStore(key);
        var factory = new EncryptedSqliteConnectionFactory(options, keyStore);
        var maintenance = new SqliteDatabaseMaintenanceService(factory);
        var media = new AesGcmMediaStore(factory, keyStore);
        await maintenance.InitializeAsync(CancellationToken.None);

        var created = new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
        var reference = await media.PutAsync(
            new ContentWriteRequest("cnt_under", ContentKind.ScreenshotPng, new byte[128], created, created.AddDays(30)),
            CancellationToken.None);

        Assert.Equal(0, await media.PurgeOverQuotaAsync(1024 * 1024, CancellationToken.None));
        Assert.NotNull(await media.ReadDecryptedAsync(reference.ContentId, CancellationToken.None));
    }

    private sealed class RecordingApplier(List<RetentionSettings> applied) : IRetentionPolicyApplier
    {
        public void Apply(RetentionSettings settings) => applied.Add(settings);
    }

    private static long DirectorySize(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Sum(path => new FileInfo(path).Length);
}
