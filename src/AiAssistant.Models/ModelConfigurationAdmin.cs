using System.Text.Json;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;

namespace AiAssistant.Models;

/// <summary>
/// Reads the current LocalOffline model settings for the UI and persists edits back to models.json.
/// Only the LocalOffline endpoint's baseUrl / chatModel / embeddingModel are editable from the UI;
/// policy and any intranet/public endpoints are preserved untouched.
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
            ModelEndpointSettings endpoint = current.Endpoints[0];
            for (var i = 0; i < current.Endpoints.Count; i++)
            {
                if (current.Endpoints[i].Tier == ModelEndpointTier.LocalOffline)
                {
                    endpoint = current.Endpoints[i];
                    break;
                }
            }

            return new ModelConfigurationView(
                endpoint.BaseAddress.ToString(),
                endpoint.ChatModel,
                endpoint.EmbeddingModel);
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

        if (!Uri.TryCreate(update.BaseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme is not ("http" or "https"))
        {
            throw new ModelConfigurationException("Base URL 必须是 http/https 地址。");
        }

        if (baseUri.IsLoopback is false)
        {
            throw new ModelConfigurationException("本机模型端点必须使用 loopback 地址（如 http://127.0.0.1:11434）。");
        }

        var document = await LoadDocumentAsync(ct);
        var endpoints = new List<ModelEndpointDocument>(document.Endpoints);
        var localIndex = endpoints.FindIndex(item => item.Tier == "LocalOffline");
        ModelEndpointDocument endpoint;
        if (localIndex < 0)
        {
            endpoint = new ModelEndpointDocument();
            endpoints.Insert(0, endpoint);
        }
        else
        {
            endpoint = endpoints[localIndex];
        }

        endpoints[localIndex < 0 ? 0 : localIndex] = endpoint with
        {
            Tier = "LocalOffline",
            Provider = "ollama",
            BaseUrl = baseUri.ToString().TrimEnd('/'),
            ChatModel = update.ChatModel.Trim(),
            EmbeddingModel = update.EmbeddingModel.Trim()
        };

        await SaveDocumentAsync(document with { Endpoints = endpoints }, ct);
    }

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
