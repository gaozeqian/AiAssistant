using System.Security.Cryptography;
using System.Text;
using AiAssistant.Core.Domain;
using AiAssistant.Storage.KeyStores;
using AiAssistant.Storage.Media;
using AiAssistant.Storage.Sqlite;
using AiAssistant.Testing.Common;

namespace AiAssistant.Storage.Tests;

[Trait("Category", "Unit")]
public sealed class ReportRepositoryTests : IDisposable
{
    private readonly string _tempRoot = TempArtifactDirectory.CreatePath("storage");

    public ReportRepositoryTests() => Directory.CreateDirectory(_tempRoot);

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
    public async Task ReportRepository_AllocatesVersionsAtomically_AndStoresEncryptedMarkdown()
    {
        var options = new StorageOptions { DataDirectory = Path.Combine(_tempRoot, "reports") };
        var key = RandomNumberGenerator.GetBytes(32);
        var keyStore = new StaticKeyStore(key);
        var factory = new EncryptedSqliteConnectionFactory(options, keyStore);
        var maintenance = new SqliteDatabaseMaintenanceService(factory);
        var repository = new SqliteReportRepository(factory);
        await maintenance.InitializeAsync(CancellationToken.None);
        var date = new DateOnly(2026, 11, 20);
        const string secretMarker = "REPORT_SECRET_MARKER_AURORA_8842";

        var v1 = await repository.AppendAsync(
            date,
            $"# 日报 v1\n{secretMarker}",
            """{"EventCount":10}""",
            "gemma4:12b",
            ModelEndpointTier.LocalOffline,
            true,
            ReportTriggerSource.Manual,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        var v2 = await repository.AppendAsync(
            date,
            "# 日报 v2",
            """{"EventCount":12}""",
            "gemma4:12b",
            ModelEndpointTier.LocalOffline,
            true,
            ReportTriggerSource.Manual,
            DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None);

        Assert.Equal(1, v1.Version);
        Assert.Equal(2, v2.Version);
        var latest = await repository.GetLatestAsync(date, CancellationToken.None);
        Assert.Equal(2, latest!.Version);
        Assert.Equal("# 日报 v2", latest.Markdown);
        Assert.Equal(2, (await repository.ListVersionsAsync(date, CancellationToken.None)).Count);

        await using (var conn = await factory.OpenConnectionAsync(CancellationToken.None))
        await using (var checkpoint = conn.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await checkpoint.ExecuteNonQueryAsync();
        }

        var raw = await File.ReadAllBytesAsync(options.DatabasePath);
        Assert.DoesNotContain(secretMarker, Encoding.UTF8.GetString(raw), StringComparison.Ordinal);

        var restartedFactory = new EncryptedSqliteConnectionFactory(options, keyStore);
        var restartedRepository = new SqliteReportRepository(restartedFactory);
        var restored = await restartedRepository.GetLatestAsync(date, CancellationToken.None);
        Assert.Equal(2, restored!.Version);
        Assert.Equal("gemma4:12b", restored.ModelId);
        Assert.True(restored.RedactionApplied);
    }
}
