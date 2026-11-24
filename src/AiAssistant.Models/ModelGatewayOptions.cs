using AiAssistant.Core.Configuration;

namespace AiAssistant.Models;

/// <summary>
/// Endpoint settings for one model role. Every runtime is reached over the OpenAI-compatible
/// HTTP API, so there is no provider switch: Ollama, LM Studio, vLLM and cloud vendors are
/// distinguished only by base address, model name and key.
/// </summary>
public sealed class ModelGatewayOptions
{
    public Uri BaseAddress { get; init; } = new(ModelDefaults.LocalBaseUrl);
    public string ChatModel { get; init; } = ModelDefaults.ChatModel;

    /// <summary>
    /// Whether the chat model may reason before answering. Translated to the OpenAI-compatible
    /// <c>reasoning_effort</c> field; off keeps thinking models from spending the output budget
    /// on reasoning and returning empty content.
    /// </summary>
    public bool Think { get; init; }

    /// <summary>Optional reasoning effort (<c>low</c>/<c>medium</c>/<c>high</c>); ignored when <see cref="Think"/> is false.</summary>
    public string? ThinkingLevel { get; init; }

    public string EmbeddingModel { get; init; } = ModelDefaults.EmbeddingModel;
    public string? ApiKey { get; init; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(120);
    public bool AllowRemoteEndpoint { get; init; }

    public bool IsEndpointPermitted =>
        AllowRemoteEndpoint || (BaseAddress.IsLoopback && BaseAddress.Scheme is "http" or "https");

    public static ModelGatewayOptions FromEnvironment()
    {
        var baseUrl = Environment.GetEnvironmentVariable(EnvironmentNames.ModelBaseUrl);
        var chatModel = Environment.GetEnvironmentVariable(EnvironmentNames.ChatModel) ?? ModelDefaults.ChatModel;
        var embeddingModel = Environment.GetEnvironmentVariable(EnvironmentNames.EmbeddingModel) ?? ModelDefaults.EmbeddingModel;

        return new ModelGatewayOptions
        {
            BaseAddress = Uri.TryCreate(string.IsNullOrWhiteSpace(baseUrl) ? ModelDefaults.LocalBaseUrl : baseUrl, UriKind.Absolute, out var uri)
                ? uri
                : new Uri(ModelDefaults.LocalBaseUrl),
            ChatModel = chatModel,
            EmbeddingModel = embeddingModel,
            Think = string.Equals(
                Environment.GetEnvironmentVariable(EnvironmentNames.ModelThink),
                "true",
                StringComparison.OrdinalIgnoreCase),
            ThinkingLevel = Environment.GetEnvironmentVariable(EnvironmentNames.ModelThinkingLevel),
            ApiKey = Environment.GetEnvironmentVariable(EnvironmentNames.ModelApiKey),
            AllowRemoteEndpoint = string.Equals(
                Environment.GetEnvironmentVariable(EnvironmentNames.ModelAllowRemote),
                "true",
                StringComparison.OrdinalIgnoreCase)
        };
    }
}
