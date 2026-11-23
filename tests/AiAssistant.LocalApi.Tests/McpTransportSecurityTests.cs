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
/// Regression tests for the Streamable HTTP transport hardening of <see cref="LoopbackMcpServer"/>:
/// Origin/Host validation (DNS-rebinding defense), authentication before quota consumption and
/// MCP-Protocol-Version negotiation. Mirrors MCP specification 2026-07-28 §Security &amp; Endpoint.
/// </summary>
[Trait("Category", "Integration")]
public sealed class McpTransportSecurityTests
{
    private const string ToolsList = """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""";

    [Fact]
    public async Task CrossOriginRequest_IsRejectedWith403()
    {
        await using var harness = await SecurityHarness.StartAsync();

        var response = await harness.SendAsync(ToolsList, harness.Token, origin: "https://evil.example");

        Assert.Equal(403, response.Status);
    }

    [Fact]
    public async Task CrossOriginRequest_WithMatchingSchemeButRemoteHost_IsRejectedWith403()
    {
        await using var harness = await SecurityHarness.StartAsync();

        var response = await harness.SendAsync(ToolsList, harness.Token, origin: $"http://127.0.0.2:{harness.Port}");

        Assert.Equal(403, response.Status);
    }

    [Fact]
    public async Task TrustedLoopbackOrigin_IsAccepted()
    {
        await using var harness = await SecurityHarness.StartAsync();

        var response = await harness.SendAsync(
            ToolsList,
            harness.Token,
            origin: $"http://127.0.0.1:{harness.Port}");

        Assert.Equal(200, response.Status);
    }

    [Fact]
    public async Task ForgedHostHeader_IsRejectedWith403()
    {
        await using var harness = await SecurityHarness.StartAsync();

        var response = await harness.SendAsync(
            ToolsList,
            harness.Token,
            host: $"evil.example:{harness.Port}");

        Assert.Equal(403, response.Status);
    }

    [Fact]
    public async Task LocalhostHostAlias_IsAccepted()
    {
        await using var harness = await SecurityHarness.StartAsync();

        var response = await harness.SendAsync(
            ToolsList,
            harness.Token,
            host: $"localhost:{harness.Port}");

        Assert.Equal(200, response.Status);
    }

    [Fact]
    public async Task UnauthorizedRequests_DoNotConsumeRateLimitQuota()
    {
        // Quota is one request per minute. If authentication ran after the limiter, the second
        // unauthenticated request would already return 429 and the later authorized call would fail.
        await using var harness = await SecurityHarness.StartAsync(maxRequestsPerMinute: 1);

        Assert.Equal(401, (await harness.SendAsync(ToolsList)).Status);
        Assert.Equal(401, (await harness.SendAsync(ToolsList)).Status);
        Assert.Equal(401, (await harness.SendAsync(ToolsList)).Status);

        Assert.Equal(200, (await harness.SendAsync(ToolsList, harness.Token)).Status);
    }

    [Fact]
    public async Task UnsupportedProtocolVersionHeader_IsRejectedWith400()
    {
        await using var harness = await SecurityHarness.StartAsync();

        var response = await harness.SendAsync(
            ToolsList,
            harness.Token,
            protocolVersion: "1999-01-01");

        Assert.Equal(400, response.Status);
        using var document = JsonDocument.Parse(response.Body);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal("UnsupportedProtocolVersionError", error.GetProperty("message").GetString());
        Assert.True(error.GetProperty("data").TryGetProperty("supported", out var supported));
        Assert.Contains("2025-03-26", supported.EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public async Task SupportedProtocolVersionHeader_IsAccepted()
    {
        await using var harness = await SecurityHarness.StartAsync();

        var response = await harness.SendAsync(
            ToolsList,
            harness.Token,
            protocolVersion: McpJsonRpcDispatcher.ProtocolVersion);

        Assert.Equal(200, response.Status);
    }

    private sealed class SecurityHarness : IAsyncDisposable
    {
        private readonly HttpClient _client;

        private SecurityHarness(LoopbackMcpServer server, HttpClient client, string token)
        {
            Server = server;
            _client = client;
            Token = token;
        }

        public LoopbackMcpServer Server { get; }

        public string Token { get; }

        public int Port => Server.Port;

        public static async Task<SecurityHarness> StartAsync(int maxRequestsPerMinute = 200)
        {
            var directory = TestDirectories.Create("mcp-transport-security");
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
                    new QueryableEventRepository([]),
                    new InMemoryReportRepository(),
                    new SystemClock()),
                new SystemClock(),
                NullLogger<LoopbackMcpServer>.Instance);
            await server.StartAsync(CancellationToken.None);
            var client = new HttpClient
            {
                BaseAddress = new Uri(server.Endpoint!.Replace("/mcp", string.Empty, StringComparison.Ordinal))
            };
            return new SecurityHarness(server, client, token);
        }

        public async Task<McpSecurityResponse> SendAsync(
            string json,
            string? token = null,
            string? host = null,
            string? origin = null,
            string? protocolVersion = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            if (host is not null)
            {
                // Strongly typed setter replaces the Host value HttpClient adds from BaseAddress.
                request.Headers.Host = host;
            }

            if (origin is not null)
            {
                request.Headers.TryAddWithoutValidation("Origin", origin);
            }

            if (protocolVersion is not null)
            {
                request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", protocolVersion);
            }

            using var response = await _client.SendAsync(request, CancellationToken.None);
            return new McpSecurityResponse(
                (int)response.StatusCode,
                await response.Content.ReadAsStringAsync(CancellationToken.None));
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await Server.DisposeAsync();
        }
    }

    private sealed record McpSecurityResponse(int Status, string Body);
}
