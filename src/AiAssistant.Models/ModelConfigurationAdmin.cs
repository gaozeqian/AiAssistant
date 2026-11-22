using System.Text.Json;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;

namespace AiAssistant.Models;

/// <summary>
/// Reads the current model endpoint settings for the UI and persists edits back to models.json.
/// The UI edits one endpoint at a time, selected by tier: LocalOffline (loopback only),
/// CorporateIntranet, or PublicCloud. Policy and any endpoints not being edited are preserved.
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
            var endpoint = current.Endpoints[0];
            for (var i = 0; i < current.Endpoints.Count; i++)
            {
                if (current.Endpoints[i].Tier == current.Policy.DefaultTier)
                {
                    endpoint = current.Endpoints[i];
                    break;
                }
            }

            return ToView(endpoint);
        }
    }

    public async Task SaveAsync(ModelConfigurationView update, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);

        if (string.IsNullOrWhiteSpace(update.ChatModel))
        {
            throw new ModelConfigurationException("对话模型名不能为空。");
        }

        if (string.IsNullOrWhiteSpace(update.EmbeddingModel))
        {
            throw new ModelConfigurationException("Embedding 模型名不能为空。");
        }

        var tier = ParseTier(update.Tier);
        if (!Uri.TryCreate(update.BaseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme is not ("http" or "https"))
        {
            throw new ModelConfigurationException("Base URL 必须是 http/https 地址。");
        }

        if (tier == ModelEndpointTier.LocalOffline && !baseUri.IsLoopback)
        {
            throw new ModelConfigurationException(
                "本机模型（LocalOffline）必须使用 loopback 地址；要配置外部模型请选择「企业内网」或「公网 API」。");
        }

        var remoteAllowed = tier != ModelEndpointTier.LocalOffline;
        var provider = remoteAllowed ? "openai" : "ollama";

        var document = await LoadDocumentAsync(ct);
        var endpoints = new List<ModelEndpointDocument>(document.Endpoints);
        var tierName = tier.ToString();
        var index = endpoints.FindIndex(item => item.Tier == tierName);
        var endpoint = index < 0 ? new ModelEndpointDocument() : endpoints[index];
        var updated = endpoint with
        {
            Tier = tierName,
            Provider = provider,
            BaseUrl = baseUri.ToString().TrimEnd('/'),
            ChatModel = update.ChatModel.Trim(),
            EmbeddingModel = update.EmbeddingModel.Trim(),
            AllowRemoteEndpoint = remoteAllowed || endpoint.AllowRemoteEndpoint
        };

        if (index < 0)
        {
            endpoints.Insert(0, updated);
        }
        else
        {
            endpoints[index] = updated;
        }

        // Make the selected tier the active default and relax the matching policy flag so the
        // endpoint is usable immediately after restart. Public cloud has no separate consent gate:
        // choosing it in the UI is the explicit consent.
        var policy = document.Policy;
        if (tier == ModelEndpointTier.PublicCloud)
        {
            // Selecting public cloud in the UI is the explicit consent: allow it and disable the
            // separate runtime consent gate so it is usable immediately after restart.
            policy = policy with { AllowPublicCloud = true, RequirePublicCloudConsent = false };
        }

        policy = policy with { DefaultTier = tierName };

        await SaveDocumentAsync(document with { Endpoints = endpoints, Policy = policy }, ct);
    }

    private static ModelEndpointTier ParseTier(string tier) =>
        tier switch
        {
            "LocalOffline" => ModelEndpointTier.LocalOffline,
            "CorporateIntranet" => ModelEndpointTier.CorporateIntranet,
            "PublicCloud" => ModelEndpointTier.PublicCloud,
            _ => throw new ModelConfigurationException($"未知模型来源 '{tier}'。")
        };

    private static ModelConfigurationView ToView(ModelEndpointSettings endpoint) =>
        new(
            endpoint.BaseAddress.ToString(),
            endpoint.ChatModel,
            endpoint.EmbeddingModel,
            endpoint.Tier.ToString());

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
