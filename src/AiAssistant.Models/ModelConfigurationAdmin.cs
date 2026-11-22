using System.Text.Json;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;

namespace AiAssistant.Models;

/// <summary>
/// Reads the current chat / embedding endpoint settings for the UI and persists edits back to
/// models.json. Chat and embedding are configured independently; the chat endpoint lives in the
/// per-tier Endpoints list while the embedding endpoint is stored as the optional Embedding document.
/// Policy and any endpoint not being edited are preserved.
/// </summary>
public sealed class ModelConfigurationAdmin(
    string modelsConfigPath,
    ModelsConfiguration current) : IModelConfigurationAdmin
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _modelsConfigPath = Path.GetFullPath(modelsConfigPath);

    public ModelConfigurationView Current
    {
        get
        {
            var chatEndpoint = current.Endpoints[0];
            for (var i = 0; i < current.Endpoints.Count; i++)
            {
                if (current.Endpoints[i].Tier == current.Policy.DefaultTier)
                {
                    chatEndpoint = current.Endpoints[i];
                    break;
                }
            }

            var embeddingEndpoint = current.EmbeddingEndpoint ?? chatEndpoint;

            return new ModelConfigurationView(
                new ModelEndpointView(chatEndpoint.BaseAddress.ToString(), chatEndpoint.ChatModel, chatEndpoint.Tier.ToString()),
                new ModelEndpointView(embeddingEndpoint.BaseAddress.ToString(), embeddingEndpoint.EmbeddingModel, embeddingEndpoint.Tier.ToString()));
        }
    }

    public async Task SaveAsync(ModelConfigurationView update, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);

        var chatTier = ParseTier(update.Chat.Tier);
        ValidateEndpoint(update.Chat, chatTier, isEmbedding: false);
        var embeddingTier = ParseTier(update.Embedding.Tier);
        ValidateEndpoint(update.Embedding, embeddingTier, isEmbedding: true);

        var document = await LoadDocumentAsync(ct);

        // Chat endpoint lives in the per-tier Endpoints list.
        var endpoints = new List<ModelEndpointDocument>(document.Endpoints);
        var chatTierName = chatTier.ToString();
        var chatIndex = endpoints.FindIndex(item => item.Tier == chatTierName);
        var chatEndpoint = chatIndex < 0 ? new ModelEndpointDocument() : endpoints[chatIndex];
        var updatedChat = chatEndpoint with
        {
            Tier = chatTierName,
            Provider = chatTier == ModelEndpointTier.LocalOffline ? "ollama" : "openai",
            BaseUrl = ParseUri(update.Chat.BaseUrl).TrimEnd('/'),
            ChatModel = update.Chat.Model.Trim(),
            AllowRemoteEndpoint = chatTier != ModelEndpointTier.LocalOffline || chatEndpoint.AllowRemoteEndpoint
        };
        if (chatIndex < 0)
        {
            endpoints.Insert(0, updatedChat);
        }
        else
        {
            endpoints[chatIndex] = updatedChat;
        }

        // Embedding endpoint stored as the optional Embedding document. When embedding uses the same
        // tier as chat, keep it absent so it reuses the chat endpoint (simplest config).
        EmbeddingEndpointDocument? embedding = embeddingTier == chatTier
            ? null
            : new EmbeddingEndpointDocument
            {
                Tier = embeddingTier.ToString(),
                Provider = embeddingTier == ModelEndpointTier.LocalOffline ? "ollama" : "openai",
                BaseUrl = ParseUri(update.Embedding.BaseUrl).TrimEnd('/'),
                Model = update.Embedding.Model.Trim(),
                AllowRemoteEndpoint = embeddingTier != ModelEndpointTier.LocalOffline
            };

        // Update policy: default tier follows chat tier; public cloud unlocks on explicit selection.
        var policy = document.Policy;
        if (chatTier == ModelEndpointTier.PublicCloud || embeddingTier == ModelEndpointTier.PublicCloud)
        {
            policy = policy with { AllowPublicCloud = true, RequirePublicCloudConsent = false };
        }

        policy = policy with { DefaultTier = chatTierName };

        await SaveDocumentAsync(document with { Endpoints = endpoints, Embedding = embedding, Policy = policy }, ct);
    }

    private static void ValidateEndpoint(ModelEndpointView view, ModelEndpointTier tier, bool isEmbedding)
    {
        if (string.IsNullOrWhiteSpace(view.Model))
        {
            throw new ModelConfigurationException($"{(isEmbedding ? "Embedding" : "对话")}模型名不能为空。");
        }

        if (!Uri.TryCreate(view.BaseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme is not ("http" or "https"))
        {
            throw new ModelConfigurationException($"{(isEmbedding ? "Embedding" : "对话")} Base URL 必须是 http/https 地址。");
        }

        if (tier == ModelEndpointTier.LocalOffline && !baseUri.IsLoopback)
        {
            throw new ModelConfigurationException(
                $"本机（LocalOffline）{(isEmbedding ? "Embedding" : "对话")}端点必须使用 loopback 地址；要配置外部模型请选择「企业内网」或「公网 API」。");
        }
    }

    private static string ParseUri(string value) =>
        new Uri(value, UriKind.Absolute).ToString().TrimEnd('/');

    private static ModelEndpointTier ParseTier(string tier) =>
        tier switch
        {
            "LocalOffline" => ModelEndpointTier.LocalOffline,
            "CorporateIntranet" => ModelEndpointTier.CorporateIntranet,
            "PublicCloud" => ModelEndpointTier.PublicCloud,
            _ => throw new ModelConfigurationException($"未知模型来源 '{tier}'。")
        };

    private async Task<ModelsFileDocument> LoadDocumentAsync(CancellationToken ct)
    {
        if (!File.Exists(_modelsConfigPath))
        {
            return new ModelsFileDocument();
        }

        await using var stream = new FileStream(_modelsConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var document = await JsonSerializer.DeserializeAsync<ModelsFileDocument>(stream, JsonOptions, ct);
        if (document is null)
        {
            throw new ModelConfigurationException("models.json 为空，无法保存。");
        }

        return document;
    }

    private async Task SaveDocumentAsync(ModelsFileDocument document, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_modelsConfigPath)!);
        var tempPath = _modelsConfigPath + ".tmp";
        await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, document, JsonOptions, ct);
        }

        File.Move(tempPath, _modelsConfigPath, overwrite: true);
    }
}
