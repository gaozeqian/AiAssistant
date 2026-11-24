using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiAssistant.Application.Ports;
using Microsoft.Extensions.AI;

namespace AiAssistant.Models;

public sealed record ModelEndpointProbeResult(
    bool ChatAvailable,
    bool EmbeddingsAvailable,
    IReadOnlyList<string> InstalledModels,
    string Diagnostic);

/// <summary>
/// OpenAI-compatible HTTP backend implementing the standard Microsoft.Extensions.AI interfaces.
/// Ollama, LM Studio, vLLM and cloud vendors are all reached through the same <c>/v1</c> surface;
/// only the base address, model name and key differ. It never logs request or response bodies.
/// </summary>
public sealed class HttpModelBackend : IChatClient, IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly HttpClient _http;
    private readonly ModelGatewayOptions _options;

    public HttpModelBackend(HttpClient http, ModelGatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        _http = http;
        _options = options;
        _http.BaseAddress ??= options.BaseAddress;
        _http.Timeout = options.RequestTimeout;
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        }
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var model = options?.ModelId ?? _options.ChatModel;
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ModelGatewayException("No chat model is configured.");
        }

        var payload = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(messages
                .Select(message => (JsonNode)new JsonObject
                {
                    ["role"] = ToOpenAiRole(message.Role),
                    ["content"] = message.Text
                })
                .ToArray()),
            ["temperature"] = options?.Temperature ?? 0.2f,
            ["max_tokens"] = options?.MaxOutputTokens ?? 2048,
            ["stream"] = false
        };

        if (ReasoningEffortPolicy.Resolve(_options.Think, _options.ThinkingLevel, _options.BaseAddress) is { } reasoningEffort)
        {
            payload["reasoning_effort"] = reasoningEffort;
        }

        using var response = await _http.PostAsJsonAsync("/v1/chat/completions", payload, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var text = document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text ?? string.Empty))
        {
            ModelId = model
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var message in response.Messages)
        {
            yield return new ChatResponseUpdate(message.Role, message.Text);
        }
    }

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var model = options?.ModelId ?? _options.EmbeddingModel;
        var inputs = values.ToArray();
        var payload = new JsonObject
        {
            ["model"] = model,
            ["input"] = new JsonArray(inputs.Select(input => (JsonNode)input!).ToArray())
        };

        using var response = await _http.PostAsJsonAsync("/v1/embeddings", payload, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var embeddings = new List<Embedding<float>>(inputs.Length);
        foreach (var item in document.RootElement.GetProperty("data").EnumerateArray())
        {
            embeddings.Add(new Embedding<float>(
                item.GetProperty("embedding").EnumerateArray().Select(v => v.GetSingle()).ToArray()));
        }

        return new GeneratedEmbeddings<Embedding<float>>(embeddings);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
        // HttpClient lifetime is owned by the Host composition root.
    }

    public async Task<ModelEndpointProbeResult> ProbeAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.ChatModel))
        {
            return new ModelEndpointProbeResult(false, false, [], "Chat model is not configured.");
        }

        try
        {
            using var response = await _http.GetAsync("/v1/models", ct);
            await EnsureSuccessAsync(response, ct);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            var models = new List<string>();
            foreach (var model in document.RootElement.GetProperty("data").EnumerateArray())
            {
                if (model.TryGetProperty("id", out var id) && id.GetString() is { } value)
                {
                    models.Add(value);
                }
            }

            var chat = models.Any(model => ModelMatches(model, _options.ChatModel));
            var embeddings = models.Any(model => ModelMatches(model, _options.EmbeddingModel));
            return new ModelEndpointProbeResult(
                chat,
                embeddings,
                models,
                chat ? "模型端点已就绪。" : "配置的对话模型未安装。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ModelEndpointProbeResult(false, false, [], $"端点探测失败（{ex.GetType().Name}）。");
        }
    }

    private static bool ModelMatches(string installed, string configured) =>
        string.Equals(installed, configured, StringComparison.OrdinalIgnoreCase) ||
        installed.StartsWith($"{configured}:", StringComparison.OrdinalIgnoreCase);

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new ModelGatewayException($"Model endpoint returned HTTP {(int)response.StatusCode}.");
        }

        await Task.CompletedTask;
    }

    private static string ToOpenAiRole(ChatRole role) =>
        role == ChatRole.System ? "system" : role == ChatRole.Assistant ? "assistant" : "user";
}

public sealed class ModelGatewayException(string message, Exception? inner = null) : Exception(message, inner);
