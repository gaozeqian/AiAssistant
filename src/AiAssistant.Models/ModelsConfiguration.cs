using System.Text.Json;
using AiAssistant.Core.Configuration;
using AiAssistant.Core.Domain;

namespace AiAssistant.Models;

/// <summary>
/// models.json schema 2. It stores only the models currently in use: one chat endpoint and one
/// embedding endpoint, each with its own tier, address, model name and secret reference. There is
/// no endpoint list and no policy block; the policy gates are derived from the two tiers.
/// </summary>
internal sealed record ModelsFileDocument
{
    public int SchemaVersion { get; init; } = 2;
    public ModelEndpointDocument Chat { get; init; } = new();
    public ModelEndpointDocument? Embedding { get; init; }
}

/// <summary>One model endpoint (chat or embedding) in schema 2.</summary>
internal sealed record ModelEndpointDocument
{
    public string Tier { get; init; } = "LocalOffline";
    public string Provider { get; init; } = "ollama";
    public string BaseUrl { get; init; } = ModelDefaults.OllamaBaseUrl;
    public string Model { get; init; } = ModelDefaults.ChatModel;
    public int TimeoutSeconds { get; init; } = 120;
    public string? ApiKeySecret { get; init; }
    public string? ClientCertificatePath { get; init; }
    public string? ClientCertificateKeyPath { get; init; }
    public string? ClientCertificatePasswordSecret { get; init; }
    public string? EnterpriseCaPath { get; init; }
    public string? ProxyUrl { get; init; }
    public bool AllowRemoteEndpoint { get; init; }

    /// <summary>Whether the chat model may reason before answering (Ollama thinking models).</summary>
    public bool Think { get; init; }

    /// <summary>Optional thinking effort: low / medium / high.</summary>
    public string? ThinkingLevel { get; init; }
}

/// <summary>Legacy schema 1 document, kept readable so existing installations migrate on load.</summary>
internal sealed record ModelsFileDocumentV1
{
    public int SchemaVersion { get; init; } = 1;
    public ModelPolicyDocumentV1 Policy { get; init; } = new();
    public IReadOnlyList<ModelEndpointDocumentV1> Endpoints { get; init; } = [];
    public EmbeddingEndpointDocumentV1? Embedding { get; init; }
}

internal sealed record EmbeddingEndpointDocumentV1
{
    public string Tier { get; init; } = "LocalOffline";
    public string Provider { get; init; } = "ollama";
    public string BaseUrl { get; init; } = ModelDefaults.OllamaBaseUrl;
    public string Model { get; init; } = ModelDefaults.EmbeddingModel;
    public int TimeoutSeconds { get; init; } = 120;
    public string? ApiKeySecret { get; init; }
    public bool AllowRemoteEndpoint { get; init; }
}

internal sealed record ModelPolicyDocumentV1
{
    public bool AllowIntranet { get; init; } = true;
    public bool AllowPublicCloud { get; init; }
    public string DefaultTier { get; init; } = "LocalOffline";
    public bool AllowLocalFallback { get; init; } = true;
    public bool RequirePublicCloudConsent { get; init; } = true;
}

internal sealed record ModelEndpointDocumentV1
{
    public string Tier { get; init; } = "LocalOffline";
    public string Provider { get; init; } = "ollama";
    public string BaseUrl { get; init; } = ModelDefaults.OllamaBaseUrl;
    public string ChatModel { get; init; } = ModelDefaults.ChatModel;
    public string EmbeddingModel { get; init; } = ModelDefaults.EmbeddingModel;
    public int TimeoutSeconds { get; init; } = 120;
    public string? ApiKeySecret { get; init; }
    public string? ClientCertificatePath { get; init; }
    public string? ClientCertificateKeyPath { get; init; }
    public string? ClientCertificatePasswordSecret { get; init; }
    public string? EnterpriseCaPath { get; init; }
    public string? ProxyUrl { get; init; }
    public bool AllowRemoteEndpoint { get; init; }
    public bool Think { get; init; }
    public string? ThinkingLevel { get; init; }
}

/// <summary>Where an endpoint sits relative to the machine; drives the outbound policy gates.</summary>
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
    bool AllowRemoteEndpoint,
    bool Think = false,
    string? ThinkingLevel = null)
{
    /// <summary>Secret reference stored in models.json; the value itself never leaves the encrypted store.</summary>
    public string? ApiKeySecret { get; init; }

    /// <summary>Secret reference for the client certificate password, mirroring <see cref="ApiKeySecret"/>.</summary>
    public string? ClientCertificatePasswordSecret { get; init; }
}

public sealed record ModelsConfiguration(
    int SchemaVersion,
    ModelPolicy Policy,
    ModelEndpointSettings Chat,
    ModelEndpointSettings Embedding);

public interface IModelSecretResolver
{
    Task<string?> ResolveAsync(string reference, CancellationToken ct);
}

/// <summary>
/// Versioned models.json loader. Schema 2 holds the active chat and embedding endpoints only;
/// schema 1 files are still readable and map onto the same in-memory shape. Unknown newer
/// schema versions fail closed.
/// </summary>
public sealed class ModelsConfigurationLoader(IModelSecretResolver secretResolver)
{
    public const int SupportedSchemaVersion = 2;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<ModelsConfiguration> LoadAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return CreateEnvironmentFallback();
        }

        var schemaVersion = await ReadSchemaVersionAsync(path, ct);
        if (schemaVersion > SupportedSchemaVersion)
        {
            throw new ModelConfigurationException(
                $"models.json schema {schemaVersion} is newer than supported {SupportedSchemaVersion}.");
        }

        return schemaVersion <= 1
            ? await LoadLegacyAsync(path, ct)
            : await LoadCurrentAsync(path, ct);
    }

    private static async Task<int> ReadSchemaVersionAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("schemaVersion", out var version) &&
                   version.TryGetInt32(out var value)
                ? value
                : 1;
        }
        catch (JsonException ex)
        {
            throw new ModelConfigurationException("models.json is not valid JSON.", ex);
        }
    }

    private async Task<ModelsConfiguration> LoadCurrentAsync(string path, CancellationToken ct)
    {
        ModelsFileDocument? document;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            document = await JsonSerializer.DeserializeAsync<ModelsFileDocument>(stream, JsonOptions, ct);
        }
        catch (JsonException ex)
        {
            throw new ModelConfigurationException("models.json is not valid JSON.", ex);
        }

        if (document is null)
        {
            throw new ModelConfigurationException("models.json is empty.");
        }

        if (string.IsNullOrWhiteSpace(document.Chat.Model))
        {
            throw new ModelConfigurationException("models.json is missing the chat endpoint model.");
        }

        var chat = await ParseEndpointAsync(document.Chat, isEmbedding: false, ct);
        var embedding = document.Embedding is null
            ? DeriveEmbeddingFromChat(chat)
            : await ParseEndpointAsync(document.Embedding, isEmbedding: true, ct);

        return new ModelsConfiguration(document.SchemaVersion, BuildPolicy(chat.Tier, embedding.Tier), chat, embedding);
    }

    private async Task<ModelsConfiguration> LoadLegacyAsync(string path, CancellationToken ct)
    {
        ModelsFileDocumentV1? document;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            document = await JsonSerializer.DeserializeAsync<ModelsFileDocumentV1>(stream, JsonOptions, ct);
        }
        catch (JsonException ex)
        {
            throw new ModelConfigurationException("models.json is not valid JSON.", ex);
        }

        if (document is null)
        {
            throw new ModelConfigurationException("models.json is empty.");
        }

        if (document.Endpoints.Count == 0)
        {
            throw new ModelConfigurationException("models.json must define at least one endpoint.");
        }

        if (document.Endpoints.Select(item => item.Tier).Distinct().Count() != document.Endpoints.Count)
        {
            throw new ModelConfigurationException("models.json may contain only one endpoint per tier.");
        }

        var chatDocument = document.Endpoints.FirstOrDefault(item =>
                               string.Equals(item.Tier, document.Policy.DefaultTier, StringComparison.OrdinalIgnoreCase))
                           ?? document.Endpoints[0];
        var chat = await ParseLegacyEndpointAsync(chatDocument, ct);
        var embedding = document.Embedding is null
            ? chat with { ChatModel = string.Empty }
            : await ParseLegacyEmbeddingAsync(document.Embedding, ct);

        // Schema 1 policy is honoured on read: a locked-down file must not silently unlock cloud access.
        var policy = ParseLegacyPolicy(document.Policy, chat.Tier, embedding.Tier);
        return new ModelsConfiguration(1, policy, chat, embedding);
    }

    /// <summary>Schema 2 without an embedding document: same endpoint, default embedding model.</summary>
    private static ModelEndpointSettings DeriveEmbeddingFromChat(ModelEndpointSettings chat) =>
        chat with
        {
            ChatModel = string.Empty,
            EmbeddingModel = ModelDefaults.EmbeddingModel,
            Think = false,
            ThinkingLevel = null
        };

    private static ModelPolicy BuildPolicy(ModelEndpointTier chatTier, ModelEndpointTier embeddingTier)
    {
        // Schema 2 has no policy block: selecting a tier in the UI is the explicit decision, so the
        // corresponding gate opens and public-cloud consent is satisfied by that choice.
        var intranet = chatTier == ModelEndpointTier.CorporateIntranet || embeddingTier == ModelEndpointTier.CorporateIntranet;
        var publicCloud = chatTier == ModelEndpointTier.PublicCloud || embeddingTier == ModelEndpointTier.PublicCloud;
        return new ModelPolicy(intranet, publicCloud, chatTier, AllowLocalFallback: false, RequirePublicCloudConsent: false);
    }

    private static ModelPolicy ParseLegacyPolicy(
        ModelPolicyDocumentV1 document,
        ModelEndpointTier chatTier,
        ModelEndpointTier embeddingTier)
    {
        if (!Enum.TryParse<ModelEndpointTier>(document.DefaultTier, ignoreCase: true, out _))
        {
            throw new ModelConfigurationException($"Unknown defaultTier '{document.DefaultTier}'.");
        }

        var intranet = document.AllowIntranet ||
                       chatTier == ModelEndpointTier.CorporateIntranet ||
                       embeddingTier == ModelEndpointTier.CorporateIntranet;
        // An endpoint that is already configured is an explicit choice; keep it usable instead of
        // failing the whole file because the old policy block still says allowPublicCloud=false.
        var publicCloud = document.AllowPublicCloud ||
                          chatTier == ModelEndpointTier.PublicCloud ||
                          embeddingTier == ModelEndpointTier.PublicCloud;

        return new ModelPolicy(
            intranet,
            publicCloud,
            chatTier,
            document.AllowLocalFallback,
            publicCloud ? false : document.RequirePublicCloudConsent);
    }

    private async Task<ModelEndpointSettings> ParseLegacyEmbeddingAsync(EmbeddingEndpointDocumentV1 document, CancellationToken ct)
    {
        var tier = ParseTier(document.Tier, "embedding");
        var provider = ParseProvider(document.Provider, "embedding");
        var baseAddress = ParseBaseAddress(document.BaseUrl, tier, "embedding");
        if (string.IsNullOrWhiteSpace(document.Model))
        {
            throw new ModelConfigurationException($"Embedding tier {tier} is missing model.");
        }

        var apiKey = await ResolveSecretAsync(document.ApiKeySecret, ct);
        return new ModelEndpointSettings(
            tier,
            provider,
            baseAddress,
            string.Empty,
            document.Model,
            TimeSpan.FromSeconds(document.TimeoutSeconds),
            apiKey,
            null,
            null,
            null,
            null,
            null,
            document.AllowRemoteEndpoint)
        {
            ApiKeySecret = document.ApiKeySecret
        };
    }

    private async Task<ModelEndpointSettings> ParseLegacyEndpointAsync(ModelEndpointDocumentV1 document, CancellationToken ct)
    {
        var tier = ParseTier(document.Tier, "chat");
        var provider = ParseProvider(document.Provider, "chat");
        var baseAddress = ParseBaseAddress(document.BaseUrl, tier, "chat");
        if (string.IsNullOrWhiteSpace(document.ChatModel))
        {
            throw new ModelConfigurationException($"Tier {tier} is missing chatModel.");
        }

        if (document.TimeoutSeconds is < 1 or > 600)
        {
            throw new ModelConfigurationException($"Tier {tier} timeoutSeconds must be within 1-600.");
        }

        var proxy = ParseProxy(document.ProxyUrl, tier);
        ValidateFiles(document, tier);

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
            document.AllowRemoteEndpoint,
            document.Think,
            NormalizeThinkingLevel(document.ThinkingLevel))
        {
            ApiKeySecret = document.ApiKeySecret,
            ClientCertificatePasswordSecret = document.ClientCertificatePasswordSecret
        };
    }

    private async Task<ModelEndpointSettings> ParseEndpointAsync(
        ModelEndpointDocument document,
        bool isEmbedding,
        CancellationToken ct)
    {
        var role = isEmbedding ? "embedding" : "chat";
        var tier = ParseTier(document.Tier, role);
        var provider = ParseProvider(document.Provider, role);
        var baseAddress = ParseBaseAddress(document.BaseUrl, tier, role);
        if (string.IsNullOrWhiteSpace(document.Model))
        {
            throw new ModelConfigurationException($"models.json {role} endpoint is missing model.");
        }

        if (document.TimeoutSeconds is < 1 or > 600)
        {
            throw new ModelConfigurationException($"{role} timeoutSeconds must be within 1-600.");
        }

        var proxy = ParseProxy(document.ProxyUrl, tier);
        ValidateFiles(document, tier);

        var apiKey = await ResolveSecretAsync(document.ApiKeySecret, ct);
        var certificatePassword = await ResolveSecretAsync(document.ClientCertificatePasswordSecret, ct);
        return new ModelEndpointSettings(
            tier,
            provider,
            baseAddress,
            isEmbedding ? string.Empty : document.Model,
            isEmbedding ? document.Model : string.Empty,
            TimeSpan.FromSeconds(document.TimeoutSeconds),
            apiKey,
            document.ClientCertificatePath,
            document.ClientCertificateKeyPath,
            certificatePassword,
            document.EnterpriseCaPath,
            proxy,
            document.AllowRemoteEndpoint,
            isEmbedding ? false : document.Think,
            isEmbedding ? null : NormalizeThinkingLevel(document.ThinkingLevel))
        {
            ApiKeySecret = document.ApiKeySecret,
            ClientCertificatePasswordSecret = document.ClientCertificatePasswordSecret
        };
    }

    private static ModelEndpointTier ParseTier(string value, string role)
    {
        if (!Enum.TryParse<ModelEndpointTier>(value, ignoreCase: true, out var tier))
        {
            throw new ModelConfigurationException($"Unknown {role} tier '{value}'.");
        }

        return tier;
    }

    private static ModelProviderKind ParseProvider(string value, string role) =>
        value.ToLowerInvariant() switch
        {
            "ollama" => ModelProviderKind.Ollama,
            "openai" or "lmstudio" or "openai-compatible" => ModelProviderKind.OpenAiCompatible,
            _ => throw new ModelConfigurationException($"Unknown {role} provider '{value}'.")
        };

    private static Uri ParseBaseAddress(string value, ModelEndpointTier tier, string role)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var baseAddress) ||
            baseAddress.Scheme is not ("http" or "https"))
        {
            throw new ModelConfigurationException($"Invalid {role} base URL for tier {tier}.");
        }

        if (tier == ModelEndpointTier.LocalOffline && !baseAddress.IsLoopback)
        {
            throw new ModelConfigurationException($"LocalOffline {role} endpoint must be loopback.");
        }

        return baseAddress;
    }

    private static Uri? ParseProxy(string? value, ModelEndpointTier tier)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var proxy) || proxy.Scheme is not ("http" or "https"))
        {
            throw new ModelConfigurationException($"Tier {tier} proxyUrl is invalid.");
        }

        return proxy;
    }

    private static void ValidateFiles(ModelEndpointDocument document, ModelEndpointTier tier)
    {
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
    }

    private static void ValidateFiles(ModelEndpointDocumentV1 document, ModelEndpointTier tier)
    {
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
    }

    private static string? NormalizeThinkingLevel(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.ToLowerInvariant();

    private async Task<string?> ResolveSecretAsync(string? reference, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(reference) ? null : await secretResolver.ResolveAsync(reference, ct);

    private static ModelsConfiguration CreateEnvironmentFallback()
    {
        var options = ModelGatewayOptions.FromEnvironment();
        var chat = new ModelEndpointSettings(
            ModelEndpointTier.LocalOffline,
            options.Provider,
            options.BaseAddress,
            options.ChatModel,
            string.Empty,
            options.RequestTimeout,
            options.ApiKey,
            null,
            null,
            null,
            null,
            null,
            AllowRemoteEndpoint: options.AllowRemoteEndpoint,
            Think: options.Think,
            ThinkingLevel: options.ThinkingLevel);
        var embedding = chat with
        {
            ChatModel = string.Empty,
            EmbeddingModel = options.EmbeddingModel,
            Think = false,
            ThinkingLevel = null
        };
        return new ModelsConfiguration(
            SupportedSchemaVersion,
            new ModelPolicy(true, false, ModelEndpointTier.LocalOffline, false, false),
            chat,
            embedding);
    }
}

public sealed class ModelConfigurationException(string message, Exception? inner = null) : ArgumentException(message, inner);
