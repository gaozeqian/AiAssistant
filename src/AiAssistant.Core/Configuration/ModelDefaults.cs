namespace AiAssistant.Core.Configuration;

/// <summary>
/// Single source of truth for local model endpoints, default model names and model-family markers.
/// Referencing this type avoids fragile string duplication across gateway options, file configuration,
/// diagnostics UI and embedding-profile detection.
/// </summary>
public static class ModelDefaults
{
    /// <summary>Default loopback endpoint; any OpenAI-compatible runtime (Ollama, LM Studio, vLLM) serves it.</summary>
    public const string LocalBaseUrl = "http://127.0.0.1:11434";

    public const string ChatModel = "gemma4:12b";
    public const string EmbeddingModel = "embeddinggemma-2:740m";

    /// <summary>Model-family marker used for capability detection by explicit comparison.</summary>
    public const string EmbeddingGemmaFamily = "embeddinggemma";

    /// <summary>
    /// Resolve an embedding profile by exact family prefix instead of arbitrary substring sniffing.
    /// </summary>
    public static bool IsEmbeddingGemma(string? model) =>
        model is not null &&
        model.StartsWith(EmbeddingGemmaFamily, StringComparison.OrdinalIgnoreCase);
}
