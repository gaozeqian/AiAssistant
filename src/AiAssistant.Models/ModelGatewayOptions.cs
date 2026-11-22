namespace AiAssistant.Models;

public enum ModelProviderKind
{
    Ollama = 0,
    OpenAiCompatible = 1
}

public sealed class ModelGatewayOptions
{
    public ModelProviderKind Provider { get; init; } = ModelProviderKind.Ollama;
    public Uri BaseAddress { get; init; } = new("http://127.0.0.1:11434");
    public string ChatModel { get; init; } = "gemma4:12b";

    /// <summary>
    /// Ollama thinking models (e.g. gemma4 / qwen3) spend the output budget on internal reasoning,
    /// which makes structured reports slow and can return empty content once the cap is hit.
    /// Sending <c>think: false</c> keeps answers direct. Ignored by models without a thinking mode.
    /// </summary>
    public bool DisableThinking { get; init; } = true;
    public string EmbeddingModel { get; init; } = "embeddinggemma-2:740m";
    public string? ApiKey { get; init; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(120);
    public bool AllowRemoteEndpoint { get; init; }

    public bool IsEndpointPermitted =>
        AllowRemoteEndpoint || (BaseAddress.IsLoopback && BaseAddress.Scheme is "http" or "https");

    public static ModelGatewayOptions FromEnvironment()
    {
        var provider = string.Equals(
            Environment.GetEnvironmentVariable("AIASSISTANT_MODEL_PROVIDER"),
            "lmstudio",
            StringComparison.OrdinalIgnoreCase)
            ? ModelProviderKind.OpenAiCompatible
            : ModelProviderKind.Ollama;

        var defaultBase = provider == ModelProviderKind.Ollama
            ? "http://127.0.0.1:11434"
            : "http://127.0.0.1:1234";
        var baseUrl = Environment.GetEnvironmentVariable("AIASSISTANT_MODEL_BASE_URL");
        var chatModel = Environment.GetEnvironmentVariable("AIASSISTANT_CHAT_MODEL") ?? "gemma4:12b";
        var embeddingModel = Environment.GetEnvironmentVariable("AIASSISTANT_EMBEDDING_MODEL") ?? "embeddinggemma-2:740m";

        return new ModelGatewayOptions
        {
            Provider = provider,
            BaseAddress = Uri.TryCreate(string.IsNullOrWhiteSpace(baseUrl) ? defaultBase : baseUrl, UriKind.Absolute, out var uri)
                ? uri
                : new Uri(defaultBase),
            ChatModel = chatModel,
            EmbeddingModel = embeddingModel,
            ApiKey = Environment.GetEnvironmentVariable("AIASSISTANT_MODEL_API_KEY"),
            AllowRemoteEndpoint = string.Equals(
                Environment.GetEnvironmentVariable("AIASSISTANT_MODEL_ALLOW_REMOTE"),
                "true",
                StringComparison.OrdinalIgnoreCase)
        };
    }
}
