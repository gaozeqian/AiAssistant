using System.Security.Cryptography;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Storage.KeyStores;
using AiAssistant.Storage.Media;
using AiAssistant.Storage.Sqlite;
using AiAssistant.Testing.Common;

namespace AiAssistant.Storage.Tests;

[Trait("Category", "Unit")]
public sealed class WeeklyReportRepositoryTests : IDisposable
{
    private readonly string _tempRoot = TempArtifactDirectory.CreatePath("storage-weekly");

    public WeeklyReportRepositoryTests() => Directory.CreateDirectory(_tempRoot);

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
    public async Task WeeklyRepository_AllocatesVersionsPerWeek_AndStoresEncryptedPayload()
    {
        var (weekly, factory, _) = await CreateStorageAsync("weekly-versions");
        var weekStart = new DateOnly(2026, 11, 23);
        var weekEnd = weekStart.AddDays(6);
        const string secretMarker = "WEEKLY_SECRET_MARKER_NEBULA_7731";

        var first = await weekly.AppendAsync(
            weekStart, weekEnd, $"# 周报\n\n{secretMarker}", "{\"markdown\":\"x\"}", "gemma4:12b",
            ReportTriggerSource.Manual, DateTimeOffset.UtcNow, CancellationToken.None);
        var second = await weekly.AppendAsync(
            weekStart, weekEnd, "# 周报 v2", "{\"markdown\":\"y\"}", "gemma4:12b",
            ReportTriggerSource.ScheduledWeekly, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(1, first.Version);
        Assert.Equal(2, second.Version);

        var versions = await weekly.ListVersionsAsync(weekStart, CancellationToken.None);
        Assert.Equal(2, versions.Count);
        Assert.Equal(2, versions[0].Version);

        var latest = await weekly.GetLatestAsync(weekStart, CancellationToken.None);
        Assert.NotNull(latest);
        Assert.Equal(2, latest!.Version);
        Assert.Equal(ReportTriggerSource.ScheduledWeekly, latest.TriggerSource);

        // 落盘加密：SQLCipher 在页级加密，明文标记不能出现在原始数据库文件里。
        await using (var conn = await factory.OpenConnectionAsync(CancellationToken.None))
        await using (var checkpoint = conn.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await checkpoint.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var rawBytes = await File.ReadAllBytesAsync(
            Path.Combine(_tempRoot, "weekly-versions", "aiassistant.enc.db"),
            CancellationToken.None);
        Assert.DoesNotContain(secretMarker, System.Text.Encoding.UTF8.GetString(rawBytes), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PurgingADate_RemovesWeeklyReportsCoveringThatDate()
    {
        var (weekly, _, deletion) = await CreateStorageAsync("weekly-purge");
        var weekStart = new DateOnly(2026, 11, 23);
        var weekEnd = weekStart.AddDays(6);
        await weekly.AppendAsync(
            weekStart, weekEnd, "# 周报", "{}", "gemma4:12b",
            ReportTriggerSource.Manual, DateTimeOffset.UtcNow, CancellationToken.None);

        // 周三落在周报区间内：删除该日数据必须连带删除这份周报。
        await deletion.PurgeDateAsync(weekStart.AddDays(2), CancellationToken.None);
        Assert.Empty(await weekly.ListVersionsAsync(weekStart, CancellationToken.None));

        await weekly.AppendAsync(
            weekStart, weekEnd, "# 周报", "{}", "gemma4:12b",
            ReportTriggerSource.Manual, DateTimeOffset.UtcNow, CancellationToken.None);

        // 下一周不覆盖该区间：保留。
        await deletion.PurgeDateAsync(weekEnd.AddDays(2), CancellationToken.None);
        Assert.Single(await weekly.ListVersionsAsync(weekStart, CancellationToken.None));
    }

    [Fact]
    public async Task PurgingAll_EmptiesWeeklyReports()
    {
        var (weekly, _, deletion) = await CreateStorageAsync("weekly-purge-all");
        var weekStart = new DateOnly(2026, 11, 23);
        await weekly.AppendAsync(
            weekStart, weekStart.AddDays(6), "# 周报", "{}", "gemma4:12b",
            ReportTriggerSource.Manual, DateTimeOffset.UtcNow, CancellationToken.None);

        await deletion.PurgeAllAsync(CancellationToken.None);
        Assert.Empty(await weekly.ListVersionsAsync(weekStart, CancellationToken.None));
    }

    [Fact]
    public async Task JobQueue_KeepsDailyAndWeeklyJobsForTheSameDateSeparate()
    {
        var (_, factory, _) = await CreateStorageAsync("weekly-queue");
        var queue = new SqliteReportJobQueue(factory, new SystemClock());
        var weekStart = new DateOnly(2026, 11, 23);

        var daily = await queue.EnqueueAsync(
            new ReportJobRequest(weekStart, ReportTriggerSource.Manual), CancellationToken.None);
        var weekly = await queue.EnqueueAsync(
            new ReportJobRequest(weekStart, ReportTriggerSource.ScheduledWeekly, false, ReportJobKind.Weekly),
            CancellationToken.None);
        Assert.False(daily.DeduplicatedExistingPendingJob);
        Assert.False(weekly.DeduplicatedExistingPendingJob);

        // 同类型重复入队要去重，跨类型不能互相吞掉。
        var duplicate = await queue.EnqueueAsync(
            new ReportJobRequest(weekStart, ReportTriggerSource.Manual), CancellationToken.None);
        Assert.True(duplicate.DeduplicatedExistingPendingJob);
        Assert.Equal(daily.JobId, duplicate.JobId);

        var jobs = await queue.ListRecentJobsAsync(10, CancellationToken.None);
        Assert.Equal(2, jobs.Count);
        Assert.Contains(jobs, job => job.Kind == ReportJobKind.Weekly);
        Assert.Contains(jobs, job => job.Kind == ReportJobKind.Daily);
    }

    private async Task<(SqliteWeeklyReportRepository Weekly, EncryptedSqliteConnectionFactory Factory, DataDeletionService Deletion)>
        CreateStorageAsync(string folder)
    {
        var options = new StorageOptions { DataDirectory = Path.Combine(_tempRoot, folder) };
        var keyStore = new StaticKeyStore(RandomNumberGenerator.GetBytes(32));
        var factory = new EncryptedSqliteConnectionFactory(options, keyStore);
        await new SqliteDatabaseMaintenanceService(factory).InitializeAsync(CancellationToken.None);
        var events = new SqliteEventRepository(factory, new AesGcmMediaStore(factory, keyStore));
        return (new SqliteWeeklyReportRepository(factory), factory, new DataDeletionService(factory, events));
    }
}
