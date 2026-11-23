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
                new ModelEndpointView(
                    chatEndpoint.BaseAddress.ToString(),
                    chatEndpoint.ChatModel,
                    chatEndpoint.Tier.ToString(),
                    chatEndpoint.Think,
                    chatEndpoint.ThinkingLevel),
                new ModelEndpointView(
                    embeddingEndpoint.BaseAddress.ToString(),
                    embeddingEndpoint.EmbeddingModel,
                    embeddingEndpoint.Tier.ToString()));
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
        var chatBaseUrl = ParseUri(update.Chat.BaseUrl).TrimEnd('/');
        var updatedChat = chatEndpoint with
        {
            Tier = chatTierName,
            // Local endpoints are not necessarily Ollama: keep the configured provider, otherwise
            // probe once and fall back to an OpenAI-compatible endpoint.
            Provider = chatTier == ModelEndpointTier.LocalOffline
                ? await ResolveLocalProviderAsync(chatBaseUrl, chatIndex < 0 ? null : chatEndpoint.Provider, ct)
                : "openai",
            BaseUrl = chatBaseUrl,
            ChatModel = update.Chat.Model.Trim(),
            Think = update.Chat.Think,
            ThinkingLevel = string.IsNullOrWhiteSpace(update.Chat.ThinkingLevel)
                ? null
                : update.Chat.ThinkingLevel.ToLowerInvariant(),
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
                Provider = embeddingTier == ModelEndpointTier.LocalOffline
                    ? await ResolveLocalProviderAsync(
                        ParseUri(update.Embedding.BaseUrl).TrimEnd('/'),
                        document.Embedding?.Provider,
                        ct)
                    : "openai",
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

    /// <summary>
    /// "本机" is not tied to a specific runtime. Keep an existing provider, otherwise probe
    /// Ollama's /api/tags once and fall back to the OpenAI-compatible API (LM Studio, vLLM, ...).
    /// </summary>
    private static async Task<string> ResolveLocalProviderAsync(
        string baseUrl,
        string? existingProvider,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(existingProvider))
        {
            return existingProvider;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = await http.GetAsync(new Uri(new Uri(baseUrl), "/api/tags"), ct);
            if (response.IsSuccessStatusCode)
            {
                return "ollama";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            // 本机探测失败属预期降级：改用 OpenAI 兼容端点。
            AiAssistant.Application.Diagnostics.SuppressedExceptionLog.Trace("Models.ModelConfigurationAdmin.ResolveLocalProviderAsync", ex);
        }

        return "openai";
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
