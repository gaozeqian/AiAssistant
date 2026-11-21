using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AiAssistant.Application.Ports;
using AiAssistant.Application.UseCases;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;
using AiAssistant.LocalApi;
using AiAssistant.Processing;
using AiAssistant.Summarization;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiAssistant.Report.Tests;

public sealed class HybridSearchAndEvidenceTests
{
    private static readonly DateTimeOffset BaseUtc = new(2026, 11, 20, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Search_FusesKeywordAndVectorRecall_WithTimeFilteredEvidence()
    {
        var events = new QueryableEventRepository(
        [
            Event("e1", BaseUtc, "Rider", "迁移方案.md", "整理数据库迁移步骤"),
            Event("e2", BaseUtc.AddHours(1), "Chrome", "SQLite 迁移指南", "页级加密与迁移"),
            Event("e3", BaseUtc.AddDays(3), "Rider", "无关内容", "不包含关键词"),
            Event("e4", BaseUtc.AddDays(-10), "Rider", "迁移旧记录", "超出时间范围")
        ]);
        var vectors = new FixedVectorIndex([new VectorSearchHit("e2", 0.91f), new VectorSearchHit("e3", 0.72f)]);
        var search = new HybridSearchService(
            events,
            vectors,
            new ScriptedModelGateway("ignored"),
            new HybridSearchOptions(),
            NullLogger<HybridSearchService>.Instance);

        var result = await search.SearchAsync(
            "迁移",
            new DateOnly(2026, 11, 20),
            new DateOnly(2026, 11, 21),
            CancellationToken.None);

        Assert.True(result.HasEvidence);
        Assert.Equal(2, result.KeywordCandidateCount);
        Assert.Equal(1, result.VectorCandidateCount);
        Assert.Equal("e2", result.Evidence[0].EventId);
        Assert.Equal("E1", result.Evidence[0].EvidenceId);
        Assert.Contains("keyword", result.Evidence[0].MatchedBy, StringComparison.Ordinal);
        Assert.Contains("vector", result.Evidence[0].MatchedBy, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Evidence, item => item.EventId is "e3" or "e4");
        Assert.Equal(new DateOnly(2026, 11, 20), result.FromDate);
    }

    [Fact]
    public async Task Search_EnsuresIndexOnceBeforeVectorRecall()
    {
        var events = new QueryableEventRepository([Event("e1", BaseUtc, "Rider", "迁移方案", "整理迁移步骤")]);
        var vectors = new FixedVectorIndex([new VectorSearchHit("e1", 0.9f)]);
        var calls = 0;
        var search = new HybridSearchService(
            events,
            vectors,
            new ScriptedModelGateway("ignored"),
            new HybridSearchOptions(),
            NullLogger<HybridSearchService>.Instance,
            ensureIndexed: _ =>
            {
                Interlocked.Increment(ref calls);
                return Task.CompletedTask;
            });

        var query = new DateOnly(2026, 11, 20);
        await search.SearchAsync("迁移", query, query, CancellationToken.None);
        await search.SearchAsync("迁移", query, query, CancellationToken.None);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Answer_WithCitedEvidence_KeepsModelAnswerAndCitations()
    {
        var (answerer, _) = CreateAnswerer(
            "已完成迁移方案评审 [E1]，并在 [E2] 补充了回滚步骤。",
            [new VectorSearchHit("e1", 0.9f), new VectorSearchHit("e2", 0.8f)]);

        var answer = await answerer.AnswerAsync(
            "迁移进展如何",
            new DateOnly(2026, 11, 20),
            new DateOnly(2026, 11, 20),
            CancellationToken.None);

        Assert.False(answer.Degraded);
        Assert.Equal(new[] { "E1", "E2" }, answer.CitedEvidenceIds);
        Assert.Contains("[E1]", answer.Answer, StringComparison.Ordinal);
        Assert.Equal("fake-chat", answer.ModelId);
    }

    [Fact]
    public async Task Answer_WithoutCitations_FallsBackToExtractiveEvidence()
    {
        var (answerer, model) = CreateAnswerer(
            "已完成迁移方案评审。",
            [new VectorSearchHit("e1", 0.9f), new VectorSearchHit("e2", 0.8f)]);

        var answer = await answerer.AnswerAsync(
            "迁移进展如何",
            new DateOnly(2026, 11, 20),
            new DateOnly(2026, 11, 20),
            CancellationToken.None);

        Assert.True(answer.Degraded);
        Assert.Equal("uncited-model-answer", answer.DegradedReason);
        Assert.Equal(1, model.CompleteCalls);
        Assert.NotEmpty(answer.CitedEvidenceIds);
        Assert.Contains("根据本地证据", answer.Answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Answer_WithoutEvidence_RefusesToSpeculateAndSkipsModel()
    {
        var (answerer, model) = CreateAnswerer("不应被调用 [E1]", []);

        var answer = await answerer.AnswerAsync(
            "完全不存在的主题",
            new DateOnly(2026, 11, 20),
            new DateOnly(2026, 11, 20),
            CancellationToken.None);

        Assert.True(answer.Degraded);
        Assert.Equal("no-evidence", answer.DegradedReason);
        Assert.Equal(0, model.CompleteCalls);
        Assert.Empty(answer.Evidence);
        Assert.Contains("没有找到可验证", answer.Answer, StringComparison.Ordinal);
    }

    private static (EvidenceQuestionAnswerUseCase Answerer, ScriptedModelGateway Model) CreateAnswerer(
        string completion,
        IReadOnlyList<VectorSearchHit> vectorHits)
    {
        var events = new QueryableEventRepository(
        [
            Event("e1", BaseUtc, "Rider", "迁移方案.md", "整理数据库迁移步骤"),
            Event("e2", BaseUtc.AddMinutes(30), "Chrome", "SQLite 迁移指南", "页级加密与迁移")
        ]);
        var model = new ScriptedModelGateway(completion);
        var search = new HybridSearchService(
            events,
            new FixedVectorIndex(vectorHits),
            model,
            new HybridSearchOptions(),
            NullLogger<HybridSearchService>.Instance);
        return (new EvidenceQuestionAnswerUseCase(search, model, NullLogger<EvidenceQuestionAnswerUseCase>.Instance), model);
    }

    internal static ActivityEvent Event(string id, DateTimeOffset at, string app, string title, string? text = null) =>
        new(
            id,
            OccurredTimestamp.FromInstant(at, TimeZoneInfo.Utc),
            SourceKind.WindowActivity,
            app,
            title,
            extractedText: text);
}

public sealed class RangeReportTests
{
    private static readonly DateOnly Wednesday = new(2026, 11, 18);

    [Fact]
    public async Task WeekContaining_StartsOnMonday_AndReportCoversSevenDays()
    {
        var (from, to) = RangeReportUseCase.WeekContaining(Wednesday);

        Assert.Equal(new DateOnly(2026, 11, 16), from);
        Assert.Equal(new DateOnly(2026, 11, 22), to);
        Assert.Equal(DayOfWeek.Monday, from.DayOfWeek);

        var events = new QueryableEventRepository(
        [
            HybridSearchAndEvidenceTests.Event("e1", new DateTimeOffset(2026, 11, 16, 1, 0, 0, TimeSpan.Zero), "Rider", "周一"),
            HybridSearchAndEvidenceTests.Event("e2", new DateTimeOffset(2026, 11, 16, 2, 0, 0, TimeSpan.Zero), "Rider", "周一"),
            HybridSearchAndEvidenceTests.Event("e3", new DateTimeOffset(2026, 11, 18, 3, 0, 0, TimeSpan.Zero), "Chrome", "周三")
        ]);
        var service = new ActivityRangeReportService(
            events,
            new ScriptedModelGateway("本周完成迁移方案评审，并补齐回滚步骤。"),
            new SensitiveDataRedactor(),
            NullLogger<ActivityRangeReportService>.Instance);

        var report = await service.GenerateAsync(from, to, CancellationToken.None);

        Assert.Equal(7, report.DailyStats.Count);
        Assert.Equal(3, report.TotalEvents);
        Assert.Equal(2, report.MaxDailyEvents);
        Assert.Equal(2, report.DailyStats[0].EventCount);
        Assert.Equal(0, report.DailyStats[1].EventCount);
        Assert.Equal(0, report.DailyStats[1].BarWidthPercent(report.MaxDailyEvents));
        Assert.False(report.Degraded);
        Assert.Contains("每日统计", report.Markdown, StringComparison.Ordinal);
        Assert.Contains("迁移方案评审", report.Markdown, StringComparison.Ordinal);
        Assert.Contains("\"FromDate\":\"2026-11-16\"", report.StatsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_WritesMarkdownAndStatsSidecar()
    {
        var events = new QueryableEventRepository(
        [
            HybridSearchAndEvidenceTests.Event("e1", new DateTimeOffset(2026, 11, 17, 1, 0, 0, TimeSpan.Zero), "Rider", "开发")
        ]);
        var service = new ActivityRangeReportService(
            events,
            new ScriptedModelGateway("区间总结。"),
            new SensitiveDataRedactor(),
            NullLogger<ActivityRangeReportService>.Instance);
        var report = await service.GenerateAsync(
            new DateOnly(2026, 11, 16),
            new DateOnly(2026, 11, 22),
            CancellationToken.None);
        var directory = TestDirectories.Create("range-export");

        var useCase = new RangeReportUseCase(
            new FakeRangeReportService(),
            new SystemClock(),
            new FakeReportRasterizer());
        var export = await useCase.ExportAsync(report, directory);

        Assert.True(File.Exists(export.MarkdownPath));
        Assert.True(File.Exists(export.StatsPath));
        Assert.True(File.Exists(export.SvgPath));
        Assert.NotNull(export.PngPath);
        Assert.NotNull(export.PdfPath);
        Assert.True(File.Exists(export.PngPath));
        Assert.True(File.Exists(export.PdfPath));
        Assert.EndsWith(".stats.json", export.StatsPath, StringComparison.Ordinal);
        Assert.EndsWith(".svg", export.SvgPath, StringComparison.Ordinal);
        Assert.Contains("20261116-20261122", export.MarkdownPath, StringComparison.Ordinal);
        Assert.Contains("每日统计", await File.ReadAllTextAsync(export.MarkdownPath), StringComparison.Ordinal);
        var svg = await File.ReadAllTextAsync(export.SvgPath);
        Assert.Contains("<svg", svg, StringComparison.Ordinal);
        Assert.Contains("<rect", svg, StringComparison.Ordinal);
        Assert.Contains("2026年11月16日", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void SvgRenderer_EscapesDynamicText_AndRendersEveryDay()
    {
        var stats = Enumerable.Range(0, 7)
            .Select(index => new DailyActivityStat(
                new DateOnly(2026, 11, 16).AddDays(index),
                index == 0 ? 3 : index,
                30,
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Rider"] = index + 1 }))
            .ToArray();
        var report = new RangeReport(
            new DateOnly(2026, 11, 16),
            new DateOnly(2026, 11, 22),
            "# report",
            "{}",
            stats,
            "model<&>",
            true,
            "model-unavailable");

        var svg = RangeReportSvgRenderer.Render(report);

        Assert.Contains("model&lt;&amp;&gt;", svg, StringComparison.Ordinal);
        Assert.Contains("（统计模式）", svg, StringComparison.Ordinal);
        Assert.Equal(7, CountOccurrences(svg, " 分钟</text>"));
        Assert.Contains("11-22", svg, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string value, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }

    [Fact]
    public async Task Report_WhenModelUnavailable_DegradesToDeterministicStatistics()
    {
        var events = new QueryableEventRepository(
        [
            HybridSearchAndEvidenceTests.Event("e1", new DateTimeOffset(2026, 11, 17, 1, 0, 0, TimeSpan.Zero), "Rider", "开发")
        ]);
        var service = new ActivityRangeReportService(
            events,
            new ScriptedModelGateway("unused", operational: false),
            new SensitiveDataRedactor(),
            NullLogger<ActivityRangeReportService>.Instance);

        var report = await service.GenerateAsync(
            new DateOnly(2026, 11, 16),
            new DateOnly(2026, 11, 22),
            CancellationToken.None);

        Assert.True(report.Degraded);
        Assert.Equal("model-unavailable", report.DegradedReason);
        Assert.Equal("extractive", report.ModelId);
        Assert.Contains("统计模式", report.Markdown, StringComparison.Ordinal);
        Assert.Contains("共记录 1 条活动", report.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Report_WithNoEvents_ReturnsNoEvidenceWithoutModelCall()
    {
        var model = new ScriptedModelGateway("unused");
        var service = new ActivityRangeReportService(
            new QueryableEventRepository([]),
            model,
            new SensitiveDataRedactor(),
            NullLogger<ActivityRangeReportService>.Instance);

        var report = await service.GenerateAsync(
            new DateOnly(2026, 11, 16),
            new DateOnly(2026, 11, 22),
            CancellationToken.None);

        Assert.True(report.Degraded);
        Assert.Equal("no-evidence", report.DegradedReason);
        Assert.Equal(0, model.CompleteCalls);
        Assert.Equal(7, report.DailyStats.Count);
        Assert.Contains("没有可用的本地活动记录", report.Markdown, StringComparison.Ordinal);
    }
}

public sealed class LoopbackMcpServerTests
{
    [Fact]
    public async Task Server_WhenDisabled_DoesNotBindOrExposeToken()
    {
        var directory = TestDirectories.Create("mcp-disabled");
        var tokens = new LocalMcpTokenStore(directory, new FakeKeyStore());
        await using var server = new LoopbackMcpServer(
            new McpServerOptions { Enabled = false },
            tokens,
            new McpToolCatalog(new QueryableEventRepository([]), new InMemoryReportRepository(), new SystemClock()),
            NullLogger<LoopbackMcpServer>.Instance);

        await server.StartAsync(CancellationToken.None);

        Assert.False(server.IsRunning);
        Assert.Null(server.Endpoint);
        Assert.False(server.Status.Enabled);
        Assert.False(File.Exists(tokens.FilePath));
    }

    [Fact]
    public async Task Request_WithoutToken_IsRejectedAndCounted()
    {
        await using var harness = await McpHarness.StartAsync(maxRequestsPerMinute: 30);

        var response = await harness.PostAsync("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", token: null);

        Assert.Equal(401, response.Status);
        Assert.Equal(1L, harness.Server.Status.UnauthorizedCount);
    }

    [Fact]
    public async Task ToolsList_ExposesOnlyReadOnlyTools()
    {
        await using var harness = await McpHarness.StartAsync(maxRequestsPerMinute: 30);

        var response = await harness.PostAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", harness.Token);

        Assert.Equal(200, response.Status);
        using var document = JsonDocument.Parse(response.Body);
        var names = document.RootElement.GetProperty("result").GetProperty("tools")
            .EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString()!)
            .ToArray();
        Assert.Equal(
            new[] { "activity_stats", "get_report", "list_reports", "search_events" },
            names.OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(names, name =>
            name.Contains("write", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("delete", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("capture", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("export", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WriteToolCall_IsRejectedAsNonReadOnly()
    {
        await using var harness = await McpHarness.StartAsync(maxRequestsPerMinute: 30);

        var response = await harness.PostAsync(
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"delete_events","arguments":{}}}""",
            harness.Token);

        Assert.Equal(200, response.Status);
        using var document = JsonDocument.Parse(response.Body);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal(-32602, error.GetProperty("code").GetInt32());
        Assert.Contains("read-only", error.GetProperty("message").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1L, harness.Server.Status.ToolCallCount);
    }

    [Fact]
    public async Task RateLimit_BlocksRequestsBeyondConfiguredBudget()
    {
        await using var harness = await McpHarness.StartAsync(maxRequestsPerMinute: 2);
        const string ping = """{"jsonrpc":"2.0","id":9,"method":"ping"}""";

        Assert.Equal(200, (await harness.PostAsync(ping, harness.Token)).Status);
        Assert.Equal(200, (await harness.PostAsync(ping, harness.Token)).Status);
        var blocked = await harness.PostAsync(ping, harness.Token);

        Assert.Equal(429, blocked.Status);
        Assert.False(string.IsNullOrEmpty(blocked.RetryAfter));
        Assert.Equal(1L, harness.Server.Status.RateLimitedCount);
    }

    [Fact]
    public async Task SearchEvents_ReturnsEvidenceMetadataWithoutMediaBytes()
    {
        var media = new ContentRef("c1", ContentKind.ScreenshotPng, new string('a', 64), 2048, DateTimeOffset.UnixEpoch, null);
        var events = new QueryableEventRepository(
        [
            new ActivityEvent(
                "e1",
                OccurredTimestamp.FromInstant(new DateTimeOffset(2026, 11, 20, 1, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc),
                SourceKind.WindowActivity,
                "Rider",
                "迁移方案.md",
                summary: "整理数据库迁移步骤",
                mediaRef: media),
            new ActivityEvent(
                "e2",
                OccurredTimestamp.FromInstant(new DateTimeOffset(2026, 11, 20, 2, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc),
                SourceKind.WindowActivity,
                "Chrome",
                "迁移指南",
                summary: "无截图记录")
        ]);
        await using var harness = await McpHarness.StartAsync(maxRequestsPerMinute: 30, events);

        var response = await harness.PostAsync(
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"search_events","arguments":{"query":"迁移","fromDate":"2026-11-20","toDate":"2026-11-20"}}}""",
            harness.Token);

        Assert.Equal(200, response.Status);
        using var document = JsonDocument.Parse(response.Body);
        var text = document.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("\"mediaAvailable\":true", text, StringComparison.Ordinal);
        Assert.Contains("\"mediaAvailable\":false", text, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('a', 64), text, StringComparison.Ordinal);
        Assert.DoesNotContain("base64", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Token_IsStoredEncryptedAndRotatable()
    {
        var directory = TestDirectories.Create("mcp-token");
        using var store = new LocalMcpTokenStore(directory, new FakeKeyStore());

        var token = await store.GetOrCreateAsync(CancellationToken.None);
        var again = await store.GetOrCreateAsync(CancellationToken.None);
        var payload = await File.ReadAllBytesAsync(store.FilePath);

        Assert.Equal(token, again);
        Assert.True(payload.Length > 32);
        Assert.True(payload.AsSpan(0, 4).SequenceEqual("AIMT"u8));
        Assert.DoesNotContain(token, Encoding.UTF8.GetString(payload), StringComparison.Ordinal);

        var rotated = await store.RotateAsync(CancellationToken.None);
        Assert.NotEqual(token, rotated);
        Assert.Equal(rotated, await store.GetOrCreateAsync(CancellationToken.None));
    }

    [Fact]
    public void LoopbackGuard_RejectsRoutableAddresses()
    {
        Assert.True(LoopbackMcpServer.IsLoopbackOnly(IPAddress.Loopback));
        Assert.True(LoopbackMcpServer.IsLoopbackOnly(IPAddress.IPv6Loopback));
        Assert.False(LoopbackMcpServer.IsLoopbackOnly(IPAddress.Parse("10.0.0.5")));
        Assert.False(LoopbackMcpServer.IsLoopbackOnly(IPAddress.Parse("192.168.1.10")));
    }

    private sealed class McpHarness : IAsyncDisposable
    {
        private readonly HttpClient _client;

        private McpHarness(LoopbackMcpServer server, HttpClient client, string token)
        {
            Server = server;
            _client = client;
            Token = token;
        }

        public LoopbackMcpServer Server { get; }

        public string Token { get; }

        public static async Task<McpHarness> StartAsync(
            int maxRequestsPerMinute,
            IEventRepository? events = null)
        {
            var directory = TestDirectories.Create("mcp-server");
            var tokenStore = new LocalMcpTokenStore(directory, new FakeKeyStore());
            var token = await tokenStore.GetOrCreateAsync(CancellationToken.None);
            var server = new LoopbackMcpServer(
                new McpServerOptions
                {
                    Enabled = true,
                    Port = 0,
                    MaxRequestsPerMinute = maxRequestsPerMinute
                },
                tokenStore,
                new McpToolCatalog(
                    events ?? new QueryableEventRepository([]),
                    new InMemoryReportRepository(),
                    new SystemClock()),
                NullLogger<LoopbackMcpServer>.Instance);
            await server.StartAsync(CancellationToken.None);
            var client = new HttpClient { BaseAddress = new Uri(server.Endpoint!.Replace("/mcp", string.Empty, StringComparison.Ordinal)) };
            return new McpHarness(server, client, token);
        }

        public async Task<HttpCallResult> PostAsync(string json, string? token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using var response = await _client.SendAsync(request, CancellationToken.None);
            var body = await response.Content.ReadAsStringAsync(CancellationToken.None);
            var retryAfter = response.Headers.TryGetValues("Retry-After", out var values)
                ? values.FirstOrDefault()
                : null;
            return new HttpCallResult((int)response.StatusCode, body, retryAfter);
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await Server.DisposeAsync();
        }
    }

    private sealed record HttpCallResult(int Status, string Body, string? RetryAfter);
}

internal static class TestDirectories
{
    public static string Create(string prefix)
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory,
            "test-temp",
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{prefix}-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}

internal sealed class FakeReportRasterizer : IReportRasterizer
{
    public byte[] RenderPng(RangeReport report) => [0x89, 0x50, 0x4E, 0x47];

    public byte[] RenderPdf(RangeReport report) => "%PDF-fake"u8.ToArray();
}

internal sealed class FakeRangeReportService : IRangeReportService
{
    public Task<RangeReport> GenerateAsync(DateOnly fromDate, DateOnly toDate, CancellationToken ct) =>
        Task.FromResult(new RangeReport(fromDate, toDate, "# report", "{}", [], "none", true, "no-evidence"));
}

internal sealed class QueryableEventRepository(IReadOnlyList<ActivityEvent> events) : IEventRepository
{
    public Task AppendBatchAsync(IReadOnlyList<ActivityEvent> items, CancellationToken ct) => Task.CompletedTask;

    public Task AppendWithMediaBatchAsync(IReadOnlyList<EventWithMediaWrite> items, CancellationToken ct) => Task.CompletedTask;

    public async IAsyncEnumerable<ActivityEvent> QueryAsync(
        EventQuery query,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        IEnumerable<ActivityEvent> filtered = events;
        if (query.LocalDate is { } date)
        {
            filtered = filtered.Where(item => item.Timestamp.LocalDate == date);
        }

        if (query.FromLocalDate is { } from)
        {
            filtered = filtered.Where(item => item.Timestamp.LocalDate >= from);
        }

        if (query.ToLocalDate is { } to)
        {
            filtered = filtered.Where(item => item.Timestamp.LocalDate <= to);
        }

        if (!string.IsNullOrWhiteSpace(query.SearchKeyword))
        {
            var keyword = query.SearchKeyword;
            filtered = filtered.Where(item =>
                item.WindowTitle.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                (item.ExtractedText?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.Summary?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        foreach (var item in filtered.Take(query.Limit))
        {
            ct.ThrowIfCancellationRequested();
            yield return item;
        }
    }

    public Task<ActivityEvent?> GetByIdAsync(string eventId, CancellationToken ct) =>
        Task.FromResult(events.FirstOrDefault(item => item.Id == eventId));

    public Task<IReadOnlyList<DateOnly>> ListActiveDatesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DateOnly>>(events.Select(item => item.Timestamp.LocalDate).Distinct().ToArray());

    public Task<bool> DeleteEventAsync(string eventId, CancellationToken ct) => Task.FromResult(false);

    public Task<int> PurgeDateAsync(DateOnly localDate, CancellationToken ct) => Task.FromResult(0);
}

internal sealed class InMemoryReportRepository : IReportRepository
{
    private readonly List<DailyReport> _reports = [];

    public Task<DailyReport?> GetLatestAsync(DateOnly localDate, CancellationToken ct) =>
        Task.FromResult(_reports.Where(report => report.LocalDate == localDate)
            .OrderByDescending(report => report.Version)
            .FirstOrDefault());

    public Task<IReadOnlyList<DailyReport>> ListVersionsAsync(DateOnly localDate, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DailyReport>>(_reports.Where(report => report.LocalDate == localDate)
            .OrderByDescending(report => report.Version)
            .ToArray());

    public Task<DailyReport> AppendAsync(
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
        var version = _reports.Where(report => report.LocalDate == localDate)
            .Select(report => report.Version)
            .DefaultIfEmpty(0)
            .Max() + 1;
        var report = new DailyReport(
            $"rpt_{version}",
            localDate,
            version,
            markdown,
            statsJson,
            modelId,
            endpointTier,
            redactionApplied,
            triggerSource,
            generatedAtUtc);
        _reports.Add(report);
        return Task.FromResult(report);
    }
}

internal sealed class ScriptedModelGateway(string completion, bool operational = true) : IModelGateway
{
    public int CompleteCalls { get; private set; }

    public CapabilityState Capability => operational
        ? CapabilityState.Supported(CapabilityKind.ModelGateway, "fake model")
        : CapabilityState.Unavailable(CapabilityKind.ModelGateway, "offline");

    public string ChatModelId => "fake-chat";

    public string EmbeddingModelId => "fake-embedding";

    public Task<ModelCompletionResult> CompleteAsync(ModelCompletionRequest request, CancellationToken ct)
    {
        CompleteCalls++;
        return Task.FromResult(new ModelCompletionResult(
            completion,
            ChatModelId,
            ModelEndpointTier.LocalOffline,
            false,
            TimeSpan.Zero));
    }

    public Task<ModelEmbeddingResult> EmbedAsync(ModelEmbeddingRequest request, CancellationToken ct) =>
        Task.FromResult(new ModelEmbeddingResult(
            request.Inputs.Select(_ => new ReadOnlyMemory<float>([0.1f, 0.2f, 0.3f])).ToArray(),
            EmbeddingModelId,
            3));

    public Task<ModelCapabilitySnapshot> ProbeAsync(CancellationToken ct) =>
        Task.FromResult(new ModelCapabilitySnapshot(
            true,
            true,
            ChatModelId,
            EmbeddingModelId,
            ModelEndpointTier.LocalOffline,
            "ok"));
}

internal sealed class FixedVectorIndex(IReadOnlyList<VectorSearchHit> hits) : IVectorIndex
{
    public Task<VectorIndexMetadata> GetMetadataAsync(CancellationToken ct) =>
        Task.FromResult(new VectorIndexMetadata("fake", false, null, hits.Count));

    public Task UpsertAsync(string eventId, ReadOnlyMemory<float> embedding, VectorModelDescriptor model, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<VectorSearchHit>> SearchAsync(
        ReadOnlyMemory<float> queryEmbedding,
        int topK,
        CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<VectorSearchHit>>(hits.Take(topK).ToArray());

    public async Task RebuildAsync(
        VectorModelDescriptor targetModel,
        Func<CancellationToken, IAsyncEnumerable<(string EventId, ReadOnlyMemory<float> Embedding)>> generator,
        CancellationToken ct)
    {
        await foreach (var _ in generator(ct))
        {
        }
    }
}

internal sealed class FakeKeyStore : IKeyStore
{
    private readonly byte[] _key = Enumerable.Range(0, 32).Select(index => (byte)index).ToArray();

    public CapabilityState Capability => CapabilityState.Supported(CapabilityKind.KeyStore, "fake key store");

    public Task<byte[]> GetOrCreateMasterKeyAsync(string keyId, CancellationToken ct) => Task.FromResult(_key.ToArray());

    public Task RotateWrapperKeyAsync(string keyId, CancellationToken ct) => Task.CompletedTask;

    public IKeyStore CreateForDirectory(string dataDirectory) => this;
}
