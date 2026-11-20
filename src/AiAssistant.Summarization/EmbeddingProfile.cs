namespace AiAssistant.Summarization;

/// <summary>
/// Resolves embedding prompt defaults for known models. Query instructions and document styles are
/// model-specific; unknown models fall back to plain text and no instruction.
/// </summary>
public static class EmbeddingProfile
{
    public const string DefaultDocumentStyle = "default";
    public const string TitleTextDocumentStyle = "title-text";

    public static string ResolveQueryPrefix(string embeddingModel, string? overrideValue = null)
    {
        if (overrideValue is not null)
        {
            return overrideValue;
        }

        if (embeddingModel.Contains("embeddinggemma", StringComparison.OrdinalIgnoreCase))
        {
            return HybridSearchOptions.EmbeddingGemmaQueryInstruction;
        }

        return embeddingModel.Contains("qwen3-embedding", StringComparison.OrdinalIgnoreCase)
            ? HybridSearchOptions.Qwen3RetrievalInstruction
            : string.Empty;
    }

    public static string ResolveDocumentStyle(string embeddingModel, string? overrideValue = null)
    {
        if (overrideValue is not null)
        {
            return overrideValue;
        }

        return embeddingModel.Contains("embeddinggemma", StringComparison.OrdinalIgnoreCase)
            ? TitleTextDocumentStyle
            : DefaultDocumentStyle;
    }
}
