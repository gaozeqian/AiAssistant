using System.Globalization;
using System.Text.Json;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;

namespace AiAssistant.Models;

/// <summary>
/// Reads the active chat / embedding endpoint settings for the UI and persists edits back to
/// models.json schema 2. Chat and embedding are independent documents, so they may share a tier
/// while pointing at different addresses, models and keys. Only the two models in use are stored.
/// </summary>
public sealed class ModelConfigurationAdmin(
    string modelsConfigPath,
    ModelsConfiguration current,
    IModelSecretAdmin? secrets = null) : IModelConfigurationAdmin
{
    /// <summary>Stable secret names so re-saving a key overwrites the same encrypted entry.</summary>
    public const string ChatSecretName = "chat-endpoint-key";

    public const string EmbeddingSecretName = "embedding-endpoint-key";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _modelsConfigPath = Path.GetFullPath(modelsConfigPath);
    private ModelsConfiguration _current = current ?? throw new ArgumentNullException(nameof(current));

    public ModelConfigurationView Current => new(
        new ModelEndpointView(
            _current.Chat.BaseAddress.ToString(),
            _current.Chat.ChatModel,
            _current.Chat.Tier.ToString(),
            _current.Chat.Think,
            _current.Chat.ThinkingLevel,
            _current.Chat.ApiKeySecret),
        new ModelEndpointView(
            _current.Embedding.BaseAddress.ToString(),
            _current.Embedding.EmbeddingModel,
            _current.Embedding.Tier.ToString(),
            ApiKeySecret: _current.Embedding.ApiKeySecret));

    public async Task SaveAsync(ModelConfigurationView update, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);

        var chatTier = ParseTier(update.Chat.Tier);
        ValidateEndpoint(update.Chat, chatTier, isEmbedding: false);
        var embeddingTier = ParseTier(update.Embedding.Tier);
        ValidateEndpoint(update.Embedding, embeddingTier, isEmbedding: true);

        var chatBaseUrl = ParseUri(update.Chat.BaseUrl).TrimEnd('/');
        var embeddingBaseUrl = ParseUri(update.Embedding.BaseUrl).TrimEnd('/');

        var chatSecret = await ApplySecretAsync(
            update.Chat,
            ChatSecretName,
            _current.Chat.ApiKeySecret,
            _current.Chat.ApiKey,
            ct);
        var embeddingSecret = await ApplySecretAsync(
            update.Embedding,
            EmbeddingSecretName,
            _current.Embedding.ApiKeySecret,
            _current.Embedding.ApiKey,
            ct);

        var chatProvider = chatTier == ModelEndpointTier.LocalOffline
            ? await ResolveLocalProviderAsync(chatBaseUrl, _current.Chat.Provider, ct)
            : ModelProviderKind.OpenAiCompatible;
        var embeddingProvider = embeddingTier == ModelEndpointTier.LocalOffline
            ? await ResolveLocalProviderAsync(embeddingBaseUrl, _current.Embedding.Provider, ct)
            : ModelProviderKind.OpenAiCompatible;

        var chat = _current.Chat with
        {
            Tier = chatTier,
            Provider = chatProvider,
            BaseAddress = new Uri(chatBaseUrl),
            ChatModel = update.Chat.Model.Trim(),
            EmbeddingModel = string.Empty,
            Think = update.Chat.Think,
            ThinkingLevel = string.IsNullOrWhiteSpace(update.Chat.ThinkingLevel)
                ? null
                : update.Chat.ThinkingLevel.ToLowerInvariant(),
            AllowRemoteEndpoint = chatTier != ModelEndpointTier.LocalOffline || _current.Chat.AllowRemoteEndpoint,
            ApiKey = chatSecret.ApiKey,
            ApiKeySecret = chatSecret.SecretReference
        };
        var embedding = _current.Embedding with
        {
            Tier = embeddingTier,
            Provider = embeddingProvider,
            BaseAddress = new Uri(embeddingBaseUrl),
            ChatModel = string.Empty,
            EmbeddingModel = update.Embedding.Model.Trim(),
            Think = false,
            ThinkingLevel = null,
            AllowRemoteEndpoint = embeddingTier != ModelEndpointTier.LocalOffline || _current.Embedding.AllowRemoteEndpoint,
            ApiKey = embeddingSecret.ApiKey,
            ApiKeySecret = embeddingSecret.SecretReference
        };

        var document = new ModelsFileDocument
        {
            SchemaVersion = ModelsConfigurationLoader.SupportedSchemaVersion,
            Chat = ToDocument(chat, chat.ChatModel),
            Embedding = ToDocument(embedding, embedding.EmbeddingModel)
        };

        await SaveDocumentAsync(document, ct);

        // Keep the in-memory snapshot in sync so a second save in the same session starts from
        // what was just written instead of the configuration loaded at startup.
        _current = new ModelsConfiguration(
            ModelsConfigurationLoader.SupportedSchemaVersion,
            _current.Policy with
            {
                DefaultTier = chatTier,
                AllowIntranet = chatTier == ModelEndpointTier.CorporateIntranet ||
                                embeddingTier == ModelEndpointTier.CorporateIntranet,
                AllowPublicCloud = chatTier == ModelEndpointTier.PublicCloud ||
                                   embeddingTier == ModelEndpointTier.PublicCloud,
                RequirePublicCloudConsent = false,
                AllowLocalFallback = false
            },
            chat,
            embedding);
    }

    private static ModelEndpointDocument ToDocument(ModelEndpointSettings settings, string model) =>
        new()
        {
            Tier = settings.Tier.ToString(),
            Provider = settings.Provider == ModelProviderKind.Ollama ? "ollama" : "openai",
            BaseUrl = settings.BaseAddress.ToString().TrimEnd('/'),
            Model = model,
            TimeoutSeconds = Math.Clamp((int)Math.Round(settings.Timeout.TotalSeconds), 1, 600),
            ApiKeySecret = settings.ApiKeySecret,
            ClientCertificatePath = settings.ClientCertificatePath,
            ClientCertificateKeyPath = settings.ClientCertificateKeyPath,
            ClientCertificatePasswordSecret = settings.ClientCertificatePasswordSecret,
            EnterpriseCaPath = settings.EnterpriseCaPath,
            ProxyUrl = settings.Proxy?.ToString(),
            AllowRemoteEndpoint = settings.AllowRemoteEndpoint,
            Think = settings.Think,
            ThinkingLevel = settings.ThinkingLevel
        };

    /// <summary>
    /// Stores a newly typed key in the encrypted secret store and returns the reference to persist.
    /// An empty input keeps the existing reference; <see cref="ModelEndpointView.ClearApiKey"/> removes it.
    /// </summary>
    private async Task<(string? SecretReference, string? ApiKey)> ApplySecretAsync(
        ModelEndpointView view,
        string secretName,
        string? currentReference,
        string? currentApiKey,
        CancellationToken ct)
    {
        if (view.ClearApiKey)
        {
            if (secrets is not null)
            {
                await secrets.DeleteAsync(secretName, ct);
            }

            return (null, null);
        }

        if (string.IsNullOrWhiteSpace(view.ApiKey))
        {
            return (currentReference, currentApiKey);
        }

        if (secrets is null)
        {
            throw new ModelConfigurationException("当前会话无法访问加密密钥库，接口密钥未保存。");
        }

        var value = view.ApiKey.Trim();
        await secrets.SetAsync(secretName, value, ct);
        return (secretName, value);
    }

    /// <summary>
    /// "本机" is not tied to a specific runtime. Keep an existing provider, otherwise probe
    /// Ollama's /api/tags once and fall back to the OpenAI-compatible API (LM Studio, vLLM, ...).
    /// </summary>
    private static async Task<ModelProviderKind> ResolveLocalProviderAsync(
        string baseUrl,
        ModelProviderKind existingProvider,
        CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = await http.GetAsync(new Uri(new Uri(baseUrl), "/api/tags"), ct);
            if (response.IsSuccessStatusCode)
            {
                return ModelProviderKind.Ollama;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            // 本机探测失败属预期降级：改用 OpenAI 兼容端点。
            AiAssistant.Application.Diagnostics.SuppressedExceptionLog.Trace("Models.ModelConfigurationAdmin.ResolveLocalProviderAsync", ex);
        }

        return existingProvider == ModelProviderKind.Ollama ? ModelProviderKind.OpenAiCompatible : existingProvider;
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

    private async Task SaveDocumentAsync(ModelsFileDocument document, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_modelsConfigPath)!);
        var tempPath = string.Create(
            CultureInfo.InvariantCulture,
            $"{_modelsConfigPath}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, ct);
            }

            File.Move(tempPath, _modelsConfigPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
