using System.Text.Json;
using AiAssistant.Core.Domain;

namespace AiAssistant.Models;

public sealed class ModelsFileDocument
{
    public int SchemaVersion { get; init; } = 1;
    public ModelPolicyDocument Policy { get; init; } = new();
    public IReadOnlyList<ModelEndpointDocument> Endpoints { get; init; } = [];
}

public sealed class ModelPolicyDocument
{
    public bool AllowIntranet { get; init; } = true;
    public bool AllowPublicCloud { get; init; }
    public string DefaultTier { get; init; } = "LocalOffline";
    public bool AllowLocalFallback { get; init; } = true;
    public bool RequirePublicCloudConsent { get; init; } = true;
}

public sealed class ModelEndpointDocument
{
    public string Tier { get; init; } = "LocalOffline";
    public string Provider { get; init; } = "ollama";
    public string BaseUrl { get; init; } = "http://127.0.0.1:11434";
    public string ChatModel { get; init; } = string.Empty;
    public string EmbeddingModel { get; init; } = "qwen3-embedding:0.6b";
    public int TimeoutSeconds { get; init; } = 120;
    public string? ApiKeySecret { get; init; }
    public string? ClientCertificatePath { get; init; }
    public string? ClientCertificateKeyPath { get; init; }
    public string? ClientCertificatePasswordSecret { get; init; }
    public string? EnterpriseCaPath { get; init; }
    public string? ProxyUrl { get; init; }
    public bool AllowRemoteEndpoint { get; init; }
}

public sealed record ModelPolicy(
    bool AllowIntranet,
    bool AllowPublicCloud,
    ModelEndpointTier DefaultTier,
    bool AllowLocalFallback,
    bool RequirePublicCloudConsent);

public sealed record ModelEndpointSettings(
    ModelEndpointTier Tier,
    ModelProviderKind Provider,
    Uri BaseAddress,
    string ChatModel,
    string EmbeddingModel,
    TimeSpan Timeout,
    string? ApiKey,
    string? ClientCertificatePath,
    string? ClientCertificateKeyPath,
    string? ClientCertificatePassword,
    string? EnterpriseCaPath,
    Uri? Proxy,
    bool AllowRemoteEndpoint);

public sealed record ModelsConfiguration(
    int SchemaVersion,
    ModelPolicy Policy,
    IReadOnlyList<ModelEndpointSettings> Endpoints);

public interface IModelSecretResolver
{
    Task<string?> ResolveAsync(string reference, CancellationToken ct);
}

/// <summary>
/// Versioned models.json loader. Secrets are referenced by name and resolved from encrypted storage or env:NAME.
/// Unknown newer schema versions fail closed.
/// </summary>
public sealed class ModelsConfigurationLoader(IModelSecretResolver secretResolver)
{
    public const int SupportedSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<ModelsConfiguration> LoadAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return CreateEnvironmentFallback();
        }

        ModelsFileDocument? document;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            document = await JsonSerializer.DeserializeAsync<ModelsFileDocument>(
                stream,
                JsonOptions,
                ct);
        }
        catch (JsonException ex)
        {
            throw new ModelConfigurationException("models.json is not valid JSON.", ex);
        }

        if (document is null)
        {
            throw new ModelConfigurationException("models.json is empty.");
        }

        if (document.SchemaVersion > SupportedSchemaVersion)
        {
            throw new ModelConfigurationException(
                $"models.json schema {document.SchemaVersion} is newer than supported {SupportedSchemaVersion}.");
        }

        var policy = ParsePolicy(document.Policy);
        var endpoints = new List<ModelEndpointSettings>();
        foreach (var endpoint in document.Endpoints)
        {
            endpoints.Add(await ParseEndpointAsync(endpoint, ct));
        }

        if (endpoints.Count == 0)
        {
            throw new ModelConfigurationException("models.json must define at least one endpoint.");
        }

        if (endpoints.Select(item => item.Tier).Distinct().Count() != endpoints.Count)
        {
            throw new ModelConfigurationException("models.json may contain only one endpoint per tier.");
        }

        if (!endpoints.Any(item => item.Tier == policy.DefaultTier))
        {
            throw new ModelConfigurationException("The default tier has no configured endpoint.");
        }

        if (!endpoints.Any(item => item.Tier == ModelEndpointTier.LocalOffline))
        {
            throw new ModelConfigurationException("A LocalOffline endpoint is required as the safety fallback.");
        }

        return new ModelsConfiguration(document.SchemaVersion, policy, endpoints);
    }

    private async Task<ModelEndpointSettings> ParseEndpointAsync(ModelEndpointDocument document, CancellationToken ct)
    {
        if (!Enum.TryParse<ModelEndpointTier>(document.Tier, ignoreCase: true, out var tier))
        {
            throw new ModelConfigurationException($"Unknown model tier '{document.Tier}'.");
        }

        var provider = document.Provider.ToLowerInvariant() switch
        {
            "ollama" => ModelProviderKind.Ollama,
            "openai" or "lmstudio" or "openai-compatible" => ModelProviderKind.OpenAiCompatible,
            _ => throw new ModelConfigurationException($"Unknown model provider '{document.Provider}'.")
        };

        if (!Uri.TryCreate(document.BaseUrl, UriKind.Absolute, out var baseAddress) ||
            baseAddress.Scheme is not ("http" or "https"))
        {
            throw new ModelConfigurationException($"Invalid base URL for tier {tier}.");
        }

        var remoteAllowed = document.AllowRemoteEndpoint || baseAddress.IsLoopback;
        if (!remoteAllowed)
        {
            throw new ModelConfigurationException($"Tier {tier} remote endpoint requires allowRemoteEndpoint=true.");
        }

        if (tier == ModelEndpointTier.LocalOffline && !baseAddress.IsLoopback && !document.AllowRemoteEndpoint)
        {
            throw new ModelConfigurationException("LocalOffline endpoint must be loopback.");
        }

        if (string.IsNullOrWhiteSpace(document.ChatModel))
        {
            throw new ModelConfigurationException($"Tier {tier} is missing chatModel.");
        }

        if (document.TimeoutSeconds is < 1 or > 600)
        {
            throw new ModelConfigurationException($"Tier {tier} timeoutSeconds must be within 1-600.");
        }

        Uri? proxy = null;
        if (!string.IsNullOrWhiteSpace(document.ProxyUrl) &&
            (!Uri.TryCreate(document.ProxyUrl, UriKind.Absolute, out proxy) || proxy.Scheme is not ("http" or "https")))
        {
            throw new ModelConfigurationException($"Tier {tier} proxyUrl is invalid.");
        }

        if (!string.IsNullOrWhiteSpace(document.ClientCertificatePath) && !File.Exists(document.ClientCertificatePath))
        {
            throw new ModelConfigurationException($"Tier {tier} client certificate file was not found.");
        }

        if (!string.IsNullOrWhiteSpace(document.ClientCertificateKeyPath) && !File.Exists(document.ClientCertificateKeyPath))
        {
            throw new ModelConfigurationException($"Tier {tier} client certificate key file was not found.");
        }

        if (!string.IsNullOrWhiteSpace(document.EnterpriseCaPath) && !File.Exists(document.EnterpriseCaPath))
        {
            throw new ModelConfigurationException($"Tier {tier} enterprise CA file was not found.");
        }

        var apiKey = await ResolveSecretAsync(document.ApiKeySecret, ct);
        var certificatePassword = await ResolveSecretAsync(document.ClientCertificatePasswordSecret, ct);
        return new ModelEndpointSettings(
            tier,
            provider,
            baseAddress,
            document.ChatModel,
            document.EmbeddingModel,
            TimeSpan.FromSeconds(document.TimeoutSeconds),
            apiKey,
            document.ClientCertificatePath,
            document.ClientCertificateKeyPath,
            certificatePassword,
            document.EnterpriseCaPath,
            proxy,
            document.AllowRemoteEndpoint);
    }

    private async Task<string?> ResolveSecretAsync(string? reference, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(reference) ? null : await secretResolver.ResolveAsync(reference, ct);

    private static ModelPolicy ParsePolicy(ModelPolicyDocument document)
    {
        if (!Enum.TryParse<ModelEndpointTier>(document.DefaultTier, ignoreCase: true, out var defaultTier))
        {
            throw new ModelConfigurationException($"Unknown defaultTier '{document.DefaultTier}'.");
        }

        if (!document.AllowIntranet && defaultTier == ModelEndpointTier.CorporateIntranet)
        {
            throw new ModelConfigurationException("defaultTier cannot be intranet when AllowIntranet=false.");
        }

        if (!document.AllowPublicCloud && defaultTier == ModelEndpointTier.PublicCloud)
        {
            throw new ModelConfigurationException("defaultTier cannot be public cloud when AllowPublicCloud=false.");
        }

        return new ModelPolicy(
            document.AllowIntranet,
            document.AllowPublicCloud,
            defaultTier,
            document.AllowLocalFallback,
            document.RequirePublicCloudConsent);
    }

    private static ModelsConfiguration CreateEnvironmentFallback()
    {
        var options = ModelGatewayOptions.FromEnvironment();
        var endpoint = new ModelEndpointSettings(
            ModelEndpointTier.LocalOffline,
            options.Provider,
            options.BaseAddress,
            options.ChatModel,
            options.EmbeddingModel,
            options.RequestTimeout,
            options.ApiKey,
            null,
            null,
            null,
            null,
            null,
            AllowRemoteEndpoint: options.AllowRemoteEndpoint);
        return new ModelsConfiguration(
            1,
            new ModelPolicy(true, false, ModelEndpointTier.LocalOffline, true, true),
            [endpoint]);
    }
}

public sealed class ModelConfigurationException(string message, Exception? inner = null) : Exception(message, inner);
