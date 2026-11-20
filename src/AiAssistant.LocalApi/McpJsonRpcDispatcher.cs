using System.Text.Json;

namespace AiAssistant.LocalApi;

public sealed record McpDispatchOutcome(
    string? Json,
    bool IsNotification,
    bool ToolInvoked = false,
    string? SessionId = null);

/// <summary>
/// Minimal JSON-RPC 2.0 dispatcher for the MCP tool subset. Notifications are acknowledged with an empty body;
/// every other request receives exactly one JSON-RPC response object.
/// </summary>
public sealed class McpJsonRpcDispatcher
{
    public const string ProtocolVersion = "2025-03-26";
    public const string ServerName = "aiassistant-local";

    private static readonly string[] SupportedProtocolVersions =
    [
        "2024-11-05",
        "2025-03-26",
        "2025-06-18"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly McpToolCatalog _tools;

    public McpJsonRpcDispatcher(McpToolCatalog tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        _tools = tools;
    }

    public async Task<McpDispatchOutcome> DispatchAsync(string body, CancellationToken ct)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return new McpDispatchOutcome(Error(null, -32700, "Parse error"), false);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new McpDispatchOutcome(Error(null, -32600, "Invalid Request"), false);
            }

            var hasId = root.TryGetProperty("id", out var idElement);
            var id = ReadId(idElement);
            if (!root.TryGetProperty("method", out var methodElement) ||
                methodElement.ValueKind != JsonValueKind.String)
            {
                return new McpDispatchOutcome(Error(id, -32600, "Invalid Request"), false);
            }

            var method = methodElement.GetString()!;
            if (!hasId)
            {
                // Notifications (e.g. notifications/initialized) never receive a response body.
                return new McpDispatchOutcome(null, true);
            }

            return method switch
            {
                "initialize" => new McpDispatchOutcome(
                    Success(id, InitializeResult(ReadRequestedProtocolVersion(root))),
                    false,
                    SessionId: Guid.NewGuid().ToString("N")),
                "ping" => new McpDispatchOutcome(Success(id, new { }), false),
                "tools/list" => new McpDispatchOutcome(Success(id, ToolsResult()), false),
                "tools/call" => new McpDispatchOutcome(await CallToolAsync(_tools, id, root, ct), false, true),
                _ => new McpDispatchOutcome(Error(id, -32601, $"Method not found: {method}"), false)
            };
        }
    }

    private static async Task<string> CallToolAsync(
        McpToolCatalog tools,
        object? id,
        JsonElement root,
        CancellationToken ct)
    {
        if (!root.TryGetProperty("params", out var parameters) ||
            parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("name", out var nameElement) ||
            nameElement.ValueKind != JsonValueKind.String)
        {
            return Error(id, -32602, "Invalid params: tools/call requires params.name.");
        }

        var name = nameElement.GetString()!;
        if (!McpToolCatalog.IsReadOnlyTool(name))
        {
            return Error(id, -32602, $"Unknown or non-read-only tool: {name}");
        }

        var arguments = parameters.TryGetProperty("arguments", out var args) ? args : default;
        try
        {
            var result = await tools.InvokeAsync(name, arguments, ct);
            return Success(id, new
            {
                content = new[] { new { type = "text", text = result.Text } },
                isError = result.IsError
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Error(id, -32603, $"Tool execution failed ({ex.GetType().Name}).");
        }
    }

    private static string? ReadRequestedProtocolVersion(JsonElement root)
    {
        if (!root.TryGetProperty("params", out var parameters) ||
            parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("protocolVersion", out var version) ||
            version.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return version.GetString();
    }

    private static object InitializeResult(string? requestedVersion) => new
    {
        protocolVersion = requestedVersion is not null && SupportedProtocolVersions.Contains(requestedVersion, StringComparer.Ordinal)
            ? requestedVersion
            : ProtocolVersion,
        capabilities = new { tools = new { listChanged = false } },
        serverInfo = new { name = ServerName, version = "1.0.0" },
        instructions = "只读本地活动检索。所有工具仅返回结构化证据，不返回截图或音频字节。"
    };

    private static object ToolsResult() => new
    {
        tools = McpToolCatalog.Tools.Select(tool => new
        {
            name = tool.Name,
            description = tool.Description,
            inputSchema = tool.InputSchema
        })
    };

    private static string Success(object? id, object result) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }, JsonOptions);

    private static string Error(object? id, int code, string message) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code, message } }, JsonOptions);

    private static object? ReadId(JsonElement id) => id.ValueKind switch
    {
        JsonValueKind.Number when id.TryGetInt64(out var number) => number,
        JsonValueKind.String => id.GetString(),
        _ => null
    };
}
