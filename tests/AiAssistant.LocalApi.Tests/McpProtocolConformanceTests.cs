using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;
using AiAssistant.LocalApi;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiAssistant.LocalApi.Tests;

/// <summary>
/// Protocol-level conformance for the local MCP endpoint: JSON-RPC shapes, version negotiation,
/// session header, notification semantics, SSE fallback, tool schema and error codes.
/// </summary>
[Trait("Category", "Integration")]
public sealed class McpProtocolConformanceTests
{
    [Fact]
    public async Task Initialize_NegotiatesSupportedVersion_AndReturnsSessionId()
    {
        await using var harness = await ConformanceHarness.StartAsync();

        var response = await harness.SendAsync(
            HttpMethod.Post,
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"conformance","version":"1.0"}}}""",
            harness.Token);

        Assert.Equal(200, response.Status);
        using var document = JsonDocument.Parse(response.Body);
        var result = document.RootElement.GetProperty("result");
        Assert.Equal("2025-06-18", result.GetProperty("protocolVersion").GetString());
        Assert.True(result.GetProperty("capabilities").TryGetProperty("tools", out _));
        Assert.Equal("aiassistant-local", result.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.True(response.Headers.ContainsKey("Mcp-Session-Id"));
        Assert.False(string.IsNullOrWhiteSpace(response.Headers["Mcp-Session-Id"]));
    }

    [Fact]
    public async Task Initialize_WithUnknownVersion_FallsBackToServerDefault()
    {
        await using var harness = await ConformanceHarness.StartAsync();

        var response = await harness.SendAsync(
            HttpMethod.Post,
            """{"jsonrpc":"2.0","id":2,"method":"initialize","params":{"protocolVersion":"1999-01-01"}}""",
            harness.Token);

        using var document = JsonDocument.Parse(response.Body);
        Assert.Equal(
            McpJsonRpcDispatcher.ProtocolVersion,
            document.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString());
    }

    [Fact]
    public async Task Notification_IsAcceptedWithoutBody()
    {
        await using var harness = await ConformanceHarness.StartAsync();

        var response = await harness.SendAsync(
            HttpMethod.Post,
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            harness.Token);

        Assert.Equal(202, response.Status);
        Assert.Equal(string.Empty, response.Body);
    }

    [Fact]
    public async Task SseOnlyClient_ReceivesSingleMessageEvent()
    {
        await using var harness = await ConformanceHarness.StartAsync();

        var response = await harness.SendAsync(
            HttpMethod.Post,
            """{"jsonrpc":"2.0","id":3,"method":"tools/list"}""",
            harness.Token,
            accept: "text/event-stream");

        Assert.Equal(200, response.Status);
        Assert.Equal("text/event-stream", response.ContentType);
        Assert.True(response.Body.StartsWith("event: message", StringComparison.Ordinal), response.Body);

        var dataLine = response.Body
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .First(line => line.StartsWith("data: ", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(dataLine["data: ".Length..]);
        Assert.Equal(4, document.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength());
    }

    [Fact]
    public async Task ClientAcceptingBoth_PrefersJsonResponse()
    {
        await using var harness = await ConformanceHarness.StartAsync();

        var response = await harness.SendAsync(
            HttpMethod.Post,
            """{"jsonrpc":"2.0","id":4,"method":"ping"}""",
            harness.Token,
            accept: "application/json, text/event-stream");

        Assert.Equal(200, response.Status);
        Assert.Equal("application/json", response.ContentType);
        using var document = JsonDocument.Parse(response.Body);
        Assert.Equal(JsonValueKind.Object, document.RootElement.GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task ToolsList_EveryToolHasObjectSchema()
    {
        await using var harness = await ConformanceHarness.StartAsync();

        var response = await harness.SendAsync(
            HttpMethod.Post,
            """{"jsonrpc":"2.0","id":5,"method":"tools/list"}""",
            harness.Token);

        using var document = JsonDocument.Parse(response.Body);
        var tools = document.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
        Assert.Equal(4, tools.Length);
        foreach (var tool in tools)
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.GetProperty("description").GetString()));
            Assert.Equal("object", tool.GetProperty("inputSchema").GetProperty("type").GetString());
        }
    }

    [Fact]
    public async Task ToolsCall_SearchEvents_ReturnsTextContentWithStructuredEvidence()
    {
        var events = new QueryableEventRepository(
        [
            new ActivityEvent(
                "conf-e1",
                OccurredTimestamp.FromInstant(new DateTimeOffset(2026, 11, 20, 1, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc),
                SourceKind.WindowActivity,
                "Rider",
                "迁移方案",
                summary: "整理迁移步骤")
        ]);
        await using var harness = await ConformanceHarness.StartAsync(events);

        var response = await harness.SendAsync(
            HttpMethod.Post,
            """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"search_events","arguments":{"query":"迁移","fromDate":"2026-11-20","toDate":"2026-11-20"}}}""",
            harness.Token);

        using var document = JsonDocument.Parse(response.Body);
        var result = document.RootElement.GetProperty("result");
        Assert.False(result.GetProperty("isError").GetBoolean());
        var content = result.GetProperty("content")[0];
        Assert.Equal("text", content.GetProperty("type").GetString());
        using var payload = JsonDocument.Parse(content.GetProperty("text").GetString()!);
        Assert.Equal(1, payload.RootElement.GetProperty("count").GetInt32());
        Assert.Equal("conf-e1", payload.RootElement.GetProperty("events")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task MalformedJson_ReturnsParseErrorWithNullId()
    {
        await using var harness = await ConformanceHarness.StartAsync();

        var response = await harness.SendAsync(HttpMethod.Post, "{", harness.Token);

        using var document = JsonDocument.Parse(response.Body);
        Assert.Equal(-32700, document.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("id").ValueKind);
    }

    [Fact]
    public async Task UnknownMethod_ReturnsMethodNotFound()
    {
        await using var harness = await ConformanceHarness.StartAsync();

        var response = await harness.SendAsync(
            HttpMethod.Post,
            """{"jsonrpc":"2.0","id":7,"method":"resources/list"}""",
            harness.Token);

        using var document = JsonDocument.Parse(response.Body);
        Assert.Equal(-32601, document.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task DeleteSession_IsAcceptedWithoutBody()
    {
        await using var harness = await ConformanceHarness.StartAsync();

        var response = await harness.SendAsync(HttpMethod.Delete, json: null, harness.Token);

        Assert.Equal(204, response.Status);
        Assert.Equal(string.Empty, response.Body);
    }

    private sealed class ConformanceHarness : IAsyncDisposable
    {
        private readonly HttpClient _client;

        private ConformanceHarness(LoopbackMcpServer server, HttpClient client, string token)
        {
            Server = server;
            _client = client;
            Token = token;
        }

        public LoopbackMcpServer Server { get; }

        public string Token { get; }

        public static async Task<ConformanceHarness> StartAsync(IEventRepository? events = null)
        {
            var directory = TestDirectories.Create("mcp-conformance");
            var tokenStore = new LocalMcpTokenStore(directory, new FakeKeyStore());
            var token = await tokenStore.GetOrCreateAsync(CancellationToken.None);
            var server = new LoopbackMcpServer(
                new McpServerOptions { Enabled = true, Port = 0, MaxRequestsPerMinute = 200 },
                tokenStore,
                new McpToolCatalog(
                    events ?? new QueryableEventRepository([]),
                    new InMemoryReportRepository(),
                    new SystemClock()),
                new SystemClock(),
                NullLogger<LoopbackMcpServer>.Instance);
            await server.StartAsync(CancellationToken.None);
            var client = new HttpClient
            {
                BaseAddress = new Uri(server.Endpoint!.Replace("/mcp", string.Empty, StringComparison.Ordinal))
            };
            return new ConformanceHarness(server, client, token);
        }

        public async Task<McpHttpResponse> SendAsync(
            HttpMethod method,
            string? json,
            string? token,
            string? accept = null)
        {
            using var request = new HttpRequestMessage(method, "/mcp");
            if (json is not null)
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            if (accept is not null)
            {
                request.Headers.TryAddWithoutValidation("Accept", accept);
            }

            using var response = await _client.SendAsync(request, CancellationToken.None);
            var body = await response.Content.ReadAsStringAsync(CancellationToken.None);
            var headers = response.Headers.ToDictionary(
                header => header.Key,
                header => string.Join(",", header.Value),
                StringComparer.OrdinalIgnoreCase);
            return new McpHttpResponse(
                (int)response.StatusCode,
                body,
                response.Content.Headers.ContentType?.MediaType,
                headers);
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await Server.DisposeAsync();
        }
    }

    private sealed record McpHttpResponse(
        int Status,
        string Body,
        string? ContentType,
        IReadOnlyDictionary<string, string> Headers);
}
