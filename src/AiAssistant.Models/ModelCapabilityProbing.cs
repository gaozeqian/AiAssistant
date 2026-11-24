using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using Microsoft.Extensions.Logging;

namespace AiAssistant.Models;

public sealed record DetailedEndpointProbe(
    ModelEndpointRole Role,
    ModelEndpointTier Tier,
    string BaseUrl,
    string ChatModel,
    string EmbeddingModel,
    bool ChatAvailable,
    bool StreamingAvailable,
    bool EmbeddingsAvailable,
    bool StructuredOutputAvailable,
    bool ToolCallingAvailable,
    string Diagnostic,
    DateTimeOffset ProbedAtUtc);

/// <summary>One endpoint to probe, tagged with the capability it serves.</summary>
public sealed record ModelProbeTarget(ModelEndpointRole Role, ModelEndpointSettings Endpoint);

public sealed class ModelCapabilityCache(string filePath) : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<DetailedEndpointProbe>> LoadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(filePath))
            {
                return [];
            }

            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await JsonSerializer.DeserializeAsync<List<DetailedEndpointProbe>>(stream, JsonOptions, ct) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(IReadOnlyList<DetailedEndpointProbe> probes, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = $"{filePath}.{Guid.NewGuid():N}.tmp";
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, probes, JsonOptions, ct);
            }

            File.Move(temp, filePath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}

/// <summary>
/// Active capability probing for chat, streaming, embeddings, structured JSON and tool calling.
/// Results are metadata-only and cached for 24 hours.
/// </summary>
public sealed class ModelCapabilityProber : IModelCapabilityProber, IDisposable
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
    private static readonly string[] EmbeddingProbeInput = ["capability probe"];
    private readonly IReadOnlyList<ModelProbeTarget> _targets;
    private readonly ModelCapabilityCache _cache;
    private readonly ILogger<ModelCapabilityProber> _logger;
    private readonly IClock _clock;
    private readonly Dictionary<ModelEndpointRole, HttpClient> _clients = [];

    public ModelCapabilityProber(
        IReadOnlyList<ModelProbeTarget> targets,
        ModelCapabilityCache cache,
        ILogger<ModelCapabilityProber> logger,
        Func<ModelEndpointSettings, HttpClient>? clientFactory = null,
        IClock? clock = null)
    {
        _targets = targets;
        _cache = cache;
        _logger = logger;
        _clock = clock ?? new SystemClock();
        foreach (var target in targets)
        {
            // Keyed by role: chat and embedding may share a tier but never an HttpClient.
            _clients[target.Role] = (clientFactory ?? ModelHttpClientFactory.Create)(target.Endpoint);
        }
    }

    public async Task<IReadOnlyList<ModelEndpointCapability>> ProbeAllAsync(bool forceRefresh, CancellationToken ct)
    {
        var cached = forceRefresh ? [] : await _cache.LoadAsync(ct);
        var results = new List<DetailedEndpointProbe>();
        foreach (var target in _targets)
        {
            ct.ThrowIfCancellationRequested();
            var endpoint = target.Endpoint;
            var existing = cached.FirstOrDefault(item =>
                item.Role == target.Role &&
                item.Tier == endpoint.Tier &&
                item.ChatModel == endpoint.ChatModel &&
                item.EmbeddingModel == endpoint.EmbeddingModel &&
                string.Equals(item.BaseUrl, endpoint.BaseAddress.ToString(), StringComparison.OrdinalIgnoreCase) &&
                _clock.UtcNow - item.ProbedAtUtc < CacheTtl);
            if (existing is not null)
            {
                results.Add(existing);
                continue;
            }

            results.Add(await ProbeEndpointAsync(target, ct));
        }

        await _cache.SaveAsync(results, ct);
        return results.Select(item => new ModelEndpointCapability(
            item.Role,
            item.Tier,
            item.BaseUrl,
            item.ChatModel,
            item.EmbeddingModel,
            item.ChatAvailable,
            item.StreamingAvailable,
            item.EmbeddingsAvailable,
            item.StructuredOutputAvailable,
            item.ToolCallingAvailable,
            item.Diagnostic,
            item.ProbedAtUtc)).ToArray();
    }

    public void Dispose()
    {
        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }

        _clients.Clear();
    }

    private async Task<DetailedEndpointProbe> ProbeEndpointAsync(ModelProbeTarget target, CancellationToken ct)
    {
        var endpoint = target.Endpoint;
        var client = _clients[target.Role];
        var isEmbeddingEndpoint = target.Role == ModelEndpointRole.Embedding;
        var chat = !isEmbeddingEndpoint && await TryChatAsync(client, endpoint, ct);
        var streaming = chat && await TryStreamingAsync(client, endpoint, ct);
        var embeddings = await TryEmbeddingsAsync(client, endpoint, ct);
        var structured = chat && await TryStructuredAsync(client, endpoint, ct);
        var tools = chat && await TryToolsAsync(client, endpoint, ct);
        var diagnostic = isEmbeddingEndpoint
            ? $"向量={(embeddings ? "可用" : "不可用")}"
            : string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"对话={chat}, 流式={streaming}, 向量={embeddings}, 结构化={structured}, 工具={tools}");
        if (!isEmbeddingEndpoint && !chat)
        {
            diagnostic += "；端点或对话模型不可用";
        }
        else if (isEmbeddingEndpoint && !embeddings)
        {
            diagnostic += "；端点或向量模型不可用";
        }

        _logger.LogInformation("Model capability probe {Role} {Tier}: {Diagnostic}", target.Role, endpoint.Tier, diagnostic);
        return new DetailedEndpointProbe(
            target.Role,
            endpoint.Tier,
            endpoint.BaseAddress.ToString(),
            endpoint.ChatModel,
            endpoint.EmbeddingModel,
            chat,
            streaming,
            embeddings,
            structured,
            tools,
            diagnostic,
            _clock.UtcNow);
    }

    private static async Task<bool> TryChatAsync(HttpClient client, ModelEndpointSettings endpoint, CancellationToken ct)
    {
        try
        {
            using var response = await client.PostAsJsonAsync(
                ChatPath(endpoint),
                BuildChatPayload(endpoint, "只回复 OK", stream: false, json: false, tools: false),
                ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<bool> TryStreamingAsync(HttpClient client, ModelEndpointSettings endpoint, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ChatPath(endpoint))
            {
                Content = JsonContent.Create(BuildChatPayload(endpoint, "回复 OK", stream: true, json: false, tools: false))
            };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            var line = await reader.ReadLineAsync(ct);
            return !string.IsNullOrWhiteSpace(line);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<bool> TryEmbeddingsAsync(HttpClient client, ModelEndpointSettings endpoint, CancellationToken ct)
    {
        try
        {
            var path = endpoint.Provider == ModelProviderKind.Ollama ? "/api/embed" : "/v1/embeddings";
            using var response = await client.PostAsJsonAsync(
                path,
                new { model = endpoint.EmbeddingModel, input = EmbeddingProbeInput },
                ct);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return document.RootElement.TryGetProperty(
                endpoint.Provider == ModelProviderKind.Ollama ? "embeddings" : "data",
                out _);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<bool> TryStructuredAsync(HttpClient client, ModelEndpointSettings endpoint, CancellationToken ct)
    {
        try
        {
            using var response = await client.PostAsJsonAsync(
                ChatPath(endpoint),
                BuildChatPayload(endpoint, "只输出 JSON：{\"ok\":true}", stream: false, json: true, tools: false),
                ct);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var text = await ExtractContentAsync(response, endpoint.Provider, ct);
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<bool> TryToolsAsync(HttpClient client, ModelEndpointSettings endpoint, CancellationToken ct)
    {
        try
        {
            using var response = await client.PostAsJsonAsync(
                ChatPath(endpoint),
                BuildChatPayload(endpoint, "请调用 noop 工具。", stream: false, json: false, tools: true),
                ct);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            return body.Contains("\"tool_calls\"", StringComparison.Ordinal) ||
                   body.Contains("\"tool_call\"", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private static JsonObject BuildChatPayload(
        ModelEndpointSettings endpoint,
        string prompt,
        bool stream,
        bool json,
        bool tools)
    {
        var payload = new JsonObject
        {
            ["model"] = endpoint.ChatModel,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = prompt }
            },
            ["stream"] = stream
        };

        if (endpoint.Provider == ModelProviderKind.Ollama)
        {
            payload["options"] = new JsonObject { ["num_predict"] = 32, ["temperature"] = 0 };
            if (json)
            {
                payload["format"] = "json";
            }
        }
        else
        {
            payload["max_tokens"] = 32;
            payload["temperature"] = 0;
            if (json)
            {
                payload["response_format"] = new JsonObject { ["type"] = "json_object" };
            }
        }

        if (tools)
        {
            payload["tools"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = "noop",
                        ["description"] = "No-op capability probe",
                        ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
                    }
                }
            };
        }

        return payload;
    }

    private static async Task<string> ExtractContentAsync(
        HttpResponseMessage response,
        ModelProviderKind provider,
        CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return provider == ModelProviderKind.Ollama
            ? document.RootElement.GetProperty("message").GetProperty("content").GetString() ?? string.Empty
            : document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
    }

    private static string ChatPath(ModelEndpointSettings endpoint) =>
        endpoint.Provider == ModelProviderKind.Ollama ? "/api/chat" : "/v1/chat/completions";
}
