using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AiAssistant.Application.Ports;
using Microsoft.Extensions.AI;

namespace AiAssistant.Models;

public sealed record ModelEndpointProbeResult(
    bool ChatAvailable,
    bool EmbeddingsAvailable,
    IReadOnlyList<string> InstalledModels,
    string Diagnostic);

/// <summary>
/// Minimal Ollama / OpenAI-compatible HTTP backend implementing the standard Microsoft.Extensions.AI interfaces.
/// It never logs request or response bodies.
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

        object payload = _options.Provider == ModelProviderKind.Ollama
            ? new
            {
                model,
                messages = messages.Select(message => new
                {
                    role = ToOllamaRole(message.Role),
                    content = message.Text
                }),
                stream = false,
                // Thinking models otherwise burn the output budget on reasoning and can return
                // empty content; Ollama ignores this flag for models without a thinking mode.
                think = !_options.DisableThinking,
                options = new
                {
                    temperature = options?.Temperature ?? 0.2,
                    num_predict = options?.MaxOutputTokens ?? 2048
                }
            }
            : new
            {
                model,
                messages = messages.Select(message => new
                {
                    role = ToOpenAiRole(message.Role),
                    content = message.Text
                }),
                temperature = options?.Temperature ?? 0.2,
                max_tokens = options?.MaxOutputTokens ?? 2048,
                stream = false
            };

        var endpoint = _options.Provider == ModelProviderKind.Ollama ? "/api/chat" : "/v1/chat/completions";
        using var response = await _http.PostAsJsonAsync(endpoint, payload, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var text = _options.Provider == ModelProviderKind.Ollama
            ? document.RootElement.GetProperty("message").GetProperty("content").GetString()
            : document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();

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
        object payload = _options.Provider == ModelProviderKind.Ollama
            ? new { model, input = inputs }
            : new { model, input = inputs };
        var endpoint = _options.Provider == ModelProviderKind.Ollama ? "/api/embed" : "/v1/embeddings";
        using var response = await _http.PostAsJsonAsync(endpoint, payload, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var embeddings = new List<Embedding<float>>(inputs.Length);
        if (_options.Provider == ModelProviderKind.Ollama)
        {
            foreach (var vector in document.RootElement.GetProperty("embeddings").EnumerateArray())
            {
                embeddings.Add(new Embedding<float>(vector.EnumerateArray().Select(v => v.GetSingle()).ToArray()));
            }
        }
        else
        {
            foreach (var item in document.RootElement.GetProperty("data").EnumerateArray())
            {
                embeddings.Add(new Embedding<float>(
                    item.GetProperty("embedding").EnumerateArray().Select(v => v.GetSingle()).ToArray()));
            }
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
            var endpoint = _options.Provider == ModelProviderKind.Ollama ? "/api/tags" : "/v1/models";
            using var response = await _http.GetAsync(endpoint, ct);
            await EnsureSuccessAsync(response, ct);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            var models = new List<string>();
            if (_options.Provider == ModelProviderKind.Ollama)
            {
                foreach (var model in document.RootElement.GetProperty("models").EnumerateArray())
                {
                    if (model.TryGetProperty("name", out var name) && name.GetString() is { } value)
                    {
                        models.Add(value);
                    }
                }
            }
            else
            {
                foreach (var model in document.RootElement.GetProperty("data").EnumerateArray())
                {
                    if (model.TryGetProperty("id", out var id) && id.GetString() is { } value)
                    {
                        models.Add(value);
                    }
                }
            }

            var chat = models.Any(model => ModelMatches(model, _options.ChatModel));
            var embeddings = models.Any(model => ModelMatches(model, _options.EmbeddingModel));
            return new ModelEndpointProbeResult(
                chat,
                embeddings,
                models,
                chat ? "Local model endpoint ready." : "Configured chat model is not installed.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ModelEndpointProbeResult(false, false, [], $"Endpoint probe failed ({ex.GetType().Name}).");
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

    private static string ToOllamaRole(ChatRole role) =>
        role == ChatRole.System ? "system" : role == ChatRole.Assistant ? "assistant" : "user";

    private static string ToOpenAiRole(ChatRole role) => ToOllamaRole(role);
}

public sealed class ModelGatewayException(string message, Exception? inner = null) : Exception(message, inner);
