using System.Net;
using System.Text;
using System.Text.Json;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Configuration;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Models;
using AiAssistant.Processing;
using AiAssistant.Storage.KeyStores;
using AiAssistant.Testing.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiAssistant.Application.Tests;

[Trait("Category", "Unit")]
public sealed class ConfigurationAndPolicyTests : IDisposable
{
    private readonly string _tempRoot = TempArtifactDirectory.CreatePath("application");

    public ConfigurationAndPolicyTests() => Directory.CreateDirectory(_tempRoot);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort test cleanup
        }
    }

    [Fact]
    public async Task ModelsConfiguration_LoadsTiersAndSecrets_AndRejectsNewerSchema()
    {
        var path = Path.Combine(_tempRoot, "models.json");
        await File.WriteAllTextAsync(path, """
            {
              "schemaVersion": 1,
              "policy": {
                "allowIntranet": true,
                "allowPublicCloud": false,
                "defaultTier": "CorporateIntranet",
                "allowLocalFallback": true
              },
              "endpoints": [
                {
                  "tier": "LocalOffline",
                  "provider": "ollama",
                  "baseUrl": "http://127.0.0.1:11434",
                  "chatModel": "gemma4:12b",
                  "embeddingModel": "embeddinggemma-2:740m"
                },
                {
                  "tier": "CorporateIntranet",
                  "provider": "openai",
                  "baseUrl": "https://llm.corp.example",
                  "chatModel": "corp-chat",
                  "embeddingModel": "corp-embedding",
                  "timeoutSeconds": 60,
                  "allowRemoteEndpoint": true,
                  "apiKeySecret": "intranet-key",
                  "proxyUrl": "http://proxy.corp.example:8080"
                }
              ]
            }
            """);
        var resolver = new FakeSecretResolver(new Dictionary<string, string> { ["intranet-key"] = "secret-value" });
        var configuration = await new ModelsConfigurationLoader(resolver).LoadAsync(path, CancellationToken.None);

        // Schema 1 still loads: the default-tier endpoint becomes the active chat endpoint and the
        // chat endpoint's embedding model becomes the active embedding model.
        Assert.Equal(ModelEndpointTier.CorporateIntranet, configuration.Policy.DefaultTier);
        Assert.Equal("corp-chat", configuration.Chat.ChatModel);
        Assert.Equal("secret-value", configuration.Chat.ApiKey);
        Assert.NotNull(configuration.Chat.Proxy);
        Assert.Equal("corp-embedding", configuration.Embedding.EmbeddingModel);

        await File.WriteAllTextAsync(path, """{"schemaVersion":99,"endpoints":[]}""");
        await Assert.ThrowsAsync<ModelConfigurationException>(() =>
            new ModelsConfigurationLoader(resolver).LoadAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task ModelsConfiguration_RejectsDuplicateTiersAndRemoteLocalEndpoint()
    {
        var resolver = new FakeSecretResolver(new Dictionary<string, string>());
        var duplicate = Path.Combine(_tempRoot, "duplicate.json");
        await File.WriteAllTextAsync(duplicate, """
            {"schemaVersion":1,"endpoints":[
              {"tier":"LocalOffline","chatModel":"a"},
              {"tier":"LocalOffline","chatModel":"b"}
            ]}
            """);
        await Assert.ThrowsAsync<ModelConfigurationException>(() =>
            new ModelsConfigurationLoader(resolver).LoadAsync(duplicate, CancellationToken.None));

        var remoteLocal = Path.Combine(_tempRoot, "remote-local.json");
        await File.WriteAllTextAsync(remoteLocal, """
            {"schemaVersion":1,"endpoints":[
              {"tier":"LocalOffline","baseUrl":"http://10.0.0.8:11434","chatModel":"a"}
            ]}
            """);
        await Assert.ThrowsAsync<ModelConfigurationException>(() =>
            new ModelsConfigurationLoader(resolver).LoadAsync(remoteLocal, CancellationToken.None));
    }

    [Fact]
    public async Task ProtectedModelSecretStore_EncryptsAndResolvesWithoutPlaintext()
    {
        var store = new ProtectedModelSecretStore(
            Path.Combine(_tempRoot, "secrets"),
            new StaticKeyStore(new byte[32]));
        using (store)
        {
            await store.SetAsync("intranet-key", "top-secret-value", CancellationToken.None);
            var raw = await File.ReadAllBytesAsync(Path.Combine(_tempRoot, "secrets", "model-secrets.enc"));
            Assert.DoesNotContain("top-secret-value", Encoding.UTF8.GetString(raw), StringComparison.Ordinal);
            Assert.Equal("top-secret-value", await store.GetAsync("intranet-key", CancellationToken.None));

            var resolver = new ModelSecretResolver(store);
            var envName = $"{EnvironmentNames.TestSecretPrefix}{Guid.NewGuid():N}";
            Environment.SetEnvironmentVariable(envName, "env-secret");
            try
            {
                Assert.Equal("env-secret", await resolver.ResolveAsync($"env:{envName}", CancellationToken.None));
            }
            finally
            {
                Environment.SetEnvironmentVariable(envName, null);
            }
        }
    }

    [Fact]
    public async Task PolicyGate_BlocksPublicCloudUntilExplicitConsent_AndCannotBeBypassed()
    {
        var inner = new RecordingGateway();
        var consent = new PublicCloudConsentGate();
        var policy = new ModelPolicy(
            AllowIntranet: true,
            AllowPublicCloud: true,
            DefaultTier: ModelEndpointTier.LocalOffline,
            AllowLocalFallback: true,
            RequirePublicCloudConsent: true);
        var gateway = new PolicyEnforcedModelGateway(inner, policy, consent);

        await Assert.ThrowsAsync<ModelPolicyViolationException>(() =>
            gateway.CompleteAsync(new ModelCompletionRequest("s", "u", ModelEndpointTier.PublicCloud), CancellationToken.None));
        Assert.Equal(0, inner.Calls);

        consent.Grant("用户显式确认公网档");
        _ = await gateway.CompleteAsync(
            new ModelCompletionRequest("s", "u", ModelEndpointTier.PublicCloud),
            CancellationToken.None);
        Assert.Equal(1, inner.Calls);

        var locked = new PolicyEnforcedModelGateway(
            inner,
            policy with { AllowPublicCloud = false },
            consent);
        await Assert.ThrowsAsync<ModelPolicyViolationException>(() =>
            locked.CompleteAsync(new ModelCompletionRequest("s", "u", ModelEndpointTier.PublicCloud), CancellationToken.None));
    }

    [Fact]
    public async Task ThreeGateGateway_RedactsInputAndOutput_AndBlocksResidualSecrets()
    {
        var inner = new RecordingGateway { ResponseText = "输出包含 user@example.com" };
        var gateway = new ThreeGateModelGateway(inner, new SensitiveDataRedactor());
        var result = await gateway.CompleteAsync(
            new ModelCompletionRequest("system", "password=Secret123 联系 user@example.com\n\n\n\n普通内容"),
            CancellationToken.None);

        Assert.DoesNotContain("Secret123", inner.LastRequest!.UserPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("user@example.com", inner.LastRequest.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("普通内容", inner.LastRequest.UserPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("user@example.com", result.Text, StringComparison.Ordinal);
        Assert.True(result.RedactionApplied);

        var noop = new ThreeGateModelGateway(new RecordingGateway(), new NoopRedactor());
        await Assert.ThrowsAsync<ModelPolicyViolationException>(() =>
            noop.CompleteAsync(
                new ModelCompletionRequest("s", "联系 user@example.com", Purpose: "test"),
                CancellationToken.None));
    }

    [Fact]
    public async Task TierRouter_DoesNotFallBackFromIntranetOrPublicCloud()
    {
        var local = new RecordingGateway { FixedTier = ModelEndpointTier.LocalOffline };
        var intranet = new FailingGateway();
        var policy = new ModelPolicy(true, true, ModelEndpointTier.CorporateIntranet, true, false);
        var router = new TierRoutingModelGateway(
            new Dictionary<ModelEndpointTier, IModelGateway>
            {
                [ModelEndpointTier.LocalOffline] = local,
                [ModelEndpointTier.CorporateIntranet] = intranet,
                [ModelEndpointTier.PublicCloud] = new FailingGateway()
            },
            policy);

        // Intranet failure surfaces as an exception instead of silently falling back to local.
        await Assert.ThrowsAsync<ModelGatewayException>(() =>
            router.CompleteAsync(new ModelCompletionRequest("s", "u", ModelEndpointTier.CorporateIntranet), CancellationToken.None));
        Assert.Equal(0, local.Calls);

        await Assert.ThrowsAsync<ModelGatewayException>(() =>
            router.CompleteAsync(new ModelCompletionRequest("s", "u", ModelEndpointTier.PublicCloud), CancellationToken.None));
    }

    [Fact]
    public async Task CapabilityProber_CachesAllFiveCapabilities_AndForceRefreshBypassesCache()
    {
        var handler = new CapabilityHandler();
        var endpoint = new ModelEndpointSettings(
            ModelEndpointTier.LocalOffline,
            ModelProviderKind.Ollama,
            new Uri("http://127.0.0.1:11434"),
            "gemma4:12b",
            "embeddinggemma-2:740m",
            TimeSpan.FromSeconds(5),
            null,
            null,
            null,
            null,
            null,
            null,
            false);
        var cache = new ModelCapabilityCache(Path.Combine(_tempRoot, "capabilities.json"));
        using (cache)
        using (var prober = new ModelCapabilityProber(
                   [new ModelProbeTarget(ModelEndpointRole.Chat, endpoint)],
                   cache,
                   NullLogger<ModelCapabilityProber>.Instance,
                   _ => new HttpClient(handler) { BaseAddress = endpoint.BaseAddress }))
        {
            var first = Assert.Single(await prober.ProbeAllAsync(forceRefresh: false, CancellationToken.None));
            Assert.True(first.ChatAvailable);
            Assert.True(first.StreamingAvailable);
            Assert.True(first.EmbeddingsAvailable);
            Assert.True(first.StructuredOutputAvailable);
            Assert.True(first.ToolCallingAvailable);
            var requestCount = handler.RequestCount;

            _ = await prober.ProbeAllAsync(forceRefresh: false, CancellationToken.None);
            Assert.Equal(requestCount, handler.RequestCount);

            _ = await prober.ProbeAllAsync(forceRefresh: true, CancellationToken.None);
            Assert.True(handler.RequestCount > requestCount);
        }
    }

    [Fact]
    public async Task ModelsConfiguration_LoadsSchema2_WithoutAnyLocalEndpoint()
    {
        // Local models are optional: a public-cloud-only configuration must load and route.
        var path = Path.Combine(_tempRoot, "models-v2.json");
        await File.WriteAllTextAsync(path, """
            {
              "schemaVersion": 2,
              "chat": {
                "tier": "PublicCloud",
                "provider": "openai",
                "baseUrl": "https://api.deepseek.com",
                "model": "deepseek-chat",
                "apiKeySecret": "chat-key",
                "allowRemoteEndpoint": true
              },
              "embedding": {
                "tier": "PublicCloud",
                "provider": "openai",
                "baseUrl": "https://api.siliconflow.cn",
                "model": "BAAI/bge-m3",
                "apiKeySecret": "embed-key",
                "allowRemoteEndpoint": true
              }
            }
            """);
        var resolver = new FakeSecretResolver(new Dictionary<string, string>
        {
            ["chat-key"] = "sk-chat",
            ["embed-key"] = "sk-embed"
        });

        var configuration = await new ModelsConfigurationLoader(resolver).LoadAsync(path, CancellationToken.None);

        Assert.Equal(2, configuration.SchemaVersion);
        Assert.Equal(ModelEndpointTier.PublicCloud, configuration.Chat.Tier);
        Assert.Equal("deepseek-chat", configuration.Chat.ChatModel);
        Assert.Equal("sk-chat", configuration.Chat.ApiKey);
        Assert.Equal("chat-key", configuration.Chat.ApiKeySecret);
        Assert.Equal("BAAI/bge-m3", configuration.Embedding.EmbeddingModel);
        Assert.Equal("sk-embed", configuration.Embedding.ApiKey);
        Assert.Equal(ModelEndpointTier.PublicCloud, configuration.Policy.DefaultTier);
        Assert.True(configuration.Policy.AllowPublicCloud);
        Assert.False(configuration.Policy.RequirePublicCloudConsent);
    }

    [Fact]
    public async Task ModelsConfiguration_Schema2_DerivesEmbeddingFromChat_WhenAbsent()
    {
        var path = Path.Combine(_tempRoot, "models-chat-only.json");
        await File.WriteAllTextAsync(path, """
            {
              "schemaVersion": 2,
              "chat": { "tier": "LocalOffline", "provider": "ollama", "baseUrl": "http://127.0.0.1:11434", "model": "gemma4:12b" }
            }
            """);

        var configuration = await new ModelsConfigurationLoader(new FakeSecretResolver(new Dictionary<string, string>()))
            .LoadAsync(path, CancellationToken.None);

        Assert.Equal("gemma4:12b", configuration.Chat.ChatModel);
        Assert.Equal("http://127.0.0.1:11434/", configuration.Embedding.BaseAddress.ToString());
        Assert.Equal(ModelDefaults.EmbeddingModel, configuration.Embedding.EmbeddingModel);
        Assert.Equal(ModelEndpointTier.LocalOffline, configuration.Embedding.Tier);
    }

    [Fact]
    public async Task ModelConfigurationAdmin_SavesOnlyActiveChatAndEmbedding()
    {
        var path = Path.Combine(_tempRoot, "models.json");
        await File.WriteAllTextAsync(path, """
            {
              "schemaVersion": 1,
              "policy": { "defaultTier": "LocalOffline" },
              "endpoints": [
                { "tier": "LocalOffline", "provider": "ollama", "baseUrl": "http://127.0.0.1:11434", "chatModel": "old-chat", "embeddingModel": "old-embed" },
                { "tier": "CorporateIntranet", "provider": "openai", "baseUrl": "https://llm.corp.example", "chatModel": "corp-chat", "embeddingModel": "corp-embed", "timeoutSeconds": 60, "allowRemoteEndpoint": true }
              ]
            }
            """);
        var configuration = await new ModelsConfigurationLoader(new FakeSecretResolver(new Dictionary<string, string>()))
            .LoadAsync(path, CancellationToken.None);
        var admin = new ModelConfigurationAdmin(path, configuration);

        Assert.Equal("old-chat", admin.Current.Chat.Model);
        await admin.SaveAsync(
            new ModelConfigurationView(
                new ModelEndpointView(
                    "http://127.0.0.1:11434",
                    "gemma4:12b-mlx",
                    "LocalOffline",
                    Think: true,
                    ThinkingLevel: "medium"),
                new ModelEndpointView("http://127.0.0.1:11434", "embeddinggemma-2:740m", "LocalOffline")),
            CancellationToken.None);

        var json = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("CorporateIntranet", json, StringComparison.Ordinal);
        Assert.Contains("\"schemaVersion\": 2", json, StringComparison.Ordinal);

        var reloaded = await new ModelsConfigurationLoader(new FakeSecretResolver(new Dictionary<string, string>()))
            .LoadAsync(path, CancellationToken.None);
        Assert.Equal("gemma4:12b-mlx", reloaded.Chat.ChatModel);
        Assert.True(reloaded.Chat.Think);
        Assert.Equal("medium", reloaded.Chat.ThinkingLevel);
        Assert.Equal("embeddinggemma-2:740m", reloaded.Embedding.EmbeddingModel);
        // The in-memory snapshot follows the save, so a second save does not resurrect old values.
        Assert.Equal("gemma4:12b-mlx", admin.Current.Chat.Model);
    }

    [Fact]
    public async Task ModelConfigurationAdmin_PreservesNonOllamaLocalProvider()
    {
        // "本机" is not tied to Ollama: an OpenAI-compatible local runtime (LM Studio, vLLM, ...) must
        // keep its provider instead of being silently rewritten to Ollama.
        var path = Path.Combine(_tempRoot, "models.json");
        await File.WriteAllTextAsync(path, """
            {
              "schemaVersion": 1,
              "policy": { "defaultTier": "LocalOffline" },
              "endpoints": [
                { "tier": "LocalOffline", "provider": "lmstudio", "baseUrl": "http://127.0.0.1:1234", "chatModel": "local-chat", "embeddingModel": "local-embed" }
              ]
            }
            """);
        var configuration = await new ModelsConfigurationLoader(new FakeSecretResolver(new Dictionary<string, string>()))
            .LoadAsync(path, CancellationToken.None);
        var admin = new ModelConfigurationAdmin(path, configuration);

        await admin.SaveAsync(
            new ModelConfigurationView(
                new ModelEndpointView("http://127.0.0.1:1234", "local-chat-2", "LocalOffline"),
                new ModelEndpointView("http://127.0.0.1:1234", "local-embed-2", "LocalOffline")),
            CancellationToken.None);

        var reloaded = await new ModelsConfigurationLoader(new FakeSecretResolver(new Dictionary<string, string>()))
            .LoadAsync(path, CancellationToken.None);
        Assert.Equal(ModelProviderKind.OpenAiCompatible, reloaded.Chat.Provider);
        Assert.Equal("local-chat-2", reloaded.Chat.ChatModel);
        Assert.Equal("local-embed-2", reloaded.Embedding.EmbeddingModel);
    }

    [Fact]
    public async Task ModelConfigurationAdmin_RejectsRemoteUrlForLocalOffline_ButAllowsRemoteTiers()
    {
        var path = Path.Combine(_tempRoot, "models.json");
        await File.WriteAllTextAsync(path, """
            {
              "schemaVersion": 1,
              "policy": { "defaultTier": "LocalOffline" },
              "endpoints": [
                { "tier": "LocalOffline", "provider": "ollama", "baseUrl": "http://127.0.0.1:11434", "chatModel": "gemma4:12b", "embeddingModel": "embeddinggemma-2:740m" }
              ]
            }
            """);
        var configuration = await new ModelsConfigurationLoader(new FakeSecretResolver(new Dictionary<string, string>()))
            .LoadAsync(path, CancellationToken.None);
        var admin = new ModelConfigurationAdmin(path, configuration);

        // LocalOffline must stay loopback.
        await Assert.ThrowsAsync<ModelConfigurationException>(() =>
            admin.SaveAsync(
                new ModelConfigurationView(
                    new ModelEndpointView("https://api.example.com/v1", "c", "LocalOffline"),
                    new ModelEndpointView("http://127.0.0.1:11434", "e", "LocalOffline")),
                CancellationToken.None));

        // CorporateIntranet accepts a remote URL and marks allowRemoteEndpoint.
        await admin.SaveAsync(
            new ModelConfigurationView(
                new ModelEndpointView("https://llm.corp.example/v1", "corp-chat", "CorporateIntranet"),
                new ModelEndpointView("http://127.0.0.1:11434", "corp-embed", "LocalOffline")),
            CancellationToken.None);
        var reloaded = await new ModelsConfigurationLoader(new FakeSecretResolver(new Dictionary<string, string>()))
            .LoadAsync(path, CancellationToken.None);
        Assert.Equal("corp-chat", reloaded.Chat.ChatModel);
        Assert.Equal(ModelEndpointTier.CorporateIntranet, reloaded.Chat.Tier);
        Assert.True(reloaded.Chat.AllowRemoteEndpoint);
        Assert.Equal(ModelProviderKind.OpenAiCompatible, reloaded.Chat.Provider);
        // The default tier follows the chat endpoint; the embedding endpoint stays local.
        Assert.Equal(ModelEndpointTier.CorporateIntranet, reloaded.Policy.DefaultTier);
        Assert.Equal(ModelEndpointTier.LocalOffline, reloaded.Embedding.Tier);
    }

    [Fact]
    public async Task ModelConfigurationAdmin_PublicCloud_EnablesCloudAndBecomesDefault()
    {
        var path = Path.Combine(_tempRoot, "models.json");
        await File.WriteAllTextAsync(path, """
            {
              "schemaVersion": 1,
              "policy": { "defaultTier": "LocalOffline" },
              "endpoints": [
                { "tier": "LocalOffline", "provider": "ollama", "baseUrl": "http://127.0.0.1:11434", "chatModel": "gemma4:12b", "embeddingModel": "embeddinggemma-2:740m" }
              ]
            }
            """);
        var configuration = await new ModelsConfigurationLoader(new FakeSecretResolver(new Dictionary<string, string>()))
            .LoadAsync(path, CancellationToken.None);
        var admin = new ModelConfigurationAdmin(path, configuration);

        await admin.SaveAsync(
            new ModelConfigurationView(
                new ModelEndpointView("https://api.example.com/v1", "cloud-chat", "PublicCloud"),
                new ModelEndpointView("https://api.example.com/v1", "cloud-embed", "PublicCloud")),
            CancellationToken.None);

        var reloaded = await new ModelsConfigurationLoader(new FakeSecretResolver(new Dictionary<string, string>()))
            .LoadAsync(path, CancellationToken.None);
        Assert.True(reloaded.Policy.AllowPublicCloud);
        Assert.False(reloaded.Policy.RequirePublicCloudConsent);
        Assert.Equal(ModelEndpointTier.PublicCloud, reloaded.Policy.DefaultTier);
        Assert.True(reloaded.Chat.AllowRemoteEndpoint);
        Assert.True(reloaded.Embedding.AllowRemoteEndpoint);
    }

    [Fact]
    public async Task ModelConfigurationAdmin_KeepsIndependentEmbedding_WhenTiersMatchButAddressesDiffer()
    {
        // Regression: the old writer dropped the embedding document whenever chat and embedding
        // shared a tier, so the embedding call silently went to the chat address and model.
        var path = Path.Combine(_tempRoot, "models.json");
        await File.WriteAllTextAsync(path, """
            {
              "schemaVersion": 1,
              "policy": { "defaultTier": "LocalOffline" },
              "endpoints": [
                { "tier": "LocalOffline", "provider": "ollama", "baseUrl": "http://127.0.0.1:11434", "chatModel": "gemma4:12b", "embeddingModel": "embeddinggemma-2:740m" }
              ]
            }
            """);
        var configuration = await new ModelsConfigurationLoader(new FakeSecretResolver(new Dictionary<string, string>()))
            .LoadAsync(path, CancellationToken.None);
        var admin = new ModelConfigurationAdmin(path, configuration);

        await admin.SaveAsync(
            new ModelConfigurationView(
                new ModelEndpointView("https://api.deepseek.com", "deepseek-chat", "PublicCloud"),
                new ModelEndpointView("https://api.siliconflow.cn", "BAAI/bge-m3", "PublicCloud")),
            CancellationToken.None);

        var reloaded = await new ModelsConfigurationLoader(new FakeSecretResolver(new Dictionary<string, string>()))
            .LoadAsync(path, CancellationToken.None);
        Assert.Equal("deepseek-chat", reloaded.Chat.ChatModel);
        Assert.Equal("https://api.deepseek.com/", reloaded.Chat.BaseAddress.ToString());
        Assert.Equal("BAAI/bge-m3", reloaded.Embedding.EmbeddingModel);
        Assert.Equal("https://api.siliconflow.cn/", reloaded.Embedding.BaseAddress.ToString());
    }

    [Fact]
    public async Task ModelConfigurationAdmin_StoresApiKeyInEncryptedStore_AndWritesOnlyTheReference()
    {
        var path = Path.Combine(_tempRoot, "models.json");
        await File.WriteAllTextAsync(path, """
            {
              "schemaVersion": 1,
              "policy": { "defaultTier": "LocalOffline" },
              "endpoints": [
                { "tier": "LocalOffline", "provider": "ollama", "baseUrl": "http://127.0.0.1:11434", "chatModel": "gemma4:12b", "embeddingModel": "embeddinggemma-2:740m" }
              ]
            }
            """);
        var secretStore = new ProtectedModelSecretStore(
            Path.Combine(_tempRoot, "secrets"),
            new StaticKeyStore(new byte[32]));
        using (secretStore)
        {
            var secretAdmin = new ModelSecretAdmin(secretStore);
            var configuration = await new ModelsConfigurationLoader(new FakeSecretResolver(new Dictionary<string, string>()))
                .LoadAsync(path, CancellationToken.None);
            var admin = new ModelConfigurationAdmin(path, configuration, secretAdmin);

            await admin.SaveAsync(
                new ModelConfigurationView(
                    new ModelEndpointView(
                        "https://api.deepseek.com",
                        "deepseek-chat",
                        "PublicCloud",
                        ApiKey: "sk-chat-secret"),
                    new ModelEndpointView(
                        "https://api.siliconflow.cn",
                        "BAAI/bge-m3",
                        "PublicCloud",
                        ApiKey: "sk-embed-secret")),
                CancellationToken.None);

            var json = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("sk-chat-secret", json, StringComparison.Ordinal);
            Assert.DoesNotContain("sk-embed-secret", json, StringComparison.Ordinal);
            Assert.Contains(ModelConfigurationAdmin.ChatSecretName, json, StringComparison.Ordinal);
            Assert.Contains(ModelConfigurationAdmin.EmbeddingSecretName, json, StringComparison.Ordinal);

            var reloaded = await new ModelsConfigurationLoader(new ModelSecretResolver(secretStore))
                .LoadAsync(path, CancellationToken.None);
            Assert.Equal("sk-chat-secret", reloaded.Chat.ApiKey);
            Assert.Equal("sk-embed-secret", reloaded.Embedding.ApiKey);

            // Clearing removes both the reference and the encrypted value.
            await admin.SaveAsync(
                new ModelConfigurationView(
                    new ModelEndpointView(
                        "https://api.deepseek.com",
                        "deepseek-chat",
                        "PublicCloud",
                        ClearApiKey: true),
                    new ModelEndpointView(
                        "https://api.siliconflow.cn",
                        "BAAI/bge-m3",
                        "PublicCloud",
                        ClearApiKey: true)),
                CancellationToken.None);
            var clearedJson = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain(ModelConfigurationAdmin.ChatSecretName, clearedJson, StringComparison.Ordinal);
            Assert.DoesNotContain(ModelConfigurationAdmin.EmbeddingSecretName, clearedJson, StringComparison.Ordinal);
            Assert.Empty(await secretAdmin.ListNamesAsync(CancellationToken.None));
        }
    }

    private sealed class FakeSecretResolver(IReadOnlyDictionary<string, string> values) : IModelSecretResolver
    {
        public Task<string?> ResolveAsync(string reference, CancellationToken ct) =>
            Task.FromResult(values.TryGetValue(reference, out var value) ? value : null);
    }

    private sealed class NoopRedactor : ITextRedactor
    {
        public TextRedaction Redact(string input) => new(input, 0);
    }

    private sealed class RecordingGateway : IModelGateway
    {
        public int Calls { get; private set; }
        public ModelCompletionRequest? LastRequest { get; private set; }
        public string ResponseText { get; init; } = "完成";
        public ModelEndpointTier FixedTier { get; init; } = ModelEndpointTier.LocalOffline;
        public CapabilityState Capability => CapabilityState.Supported(CapabilityKind.ModelGateway, "fake");
        public string ChatModelId => "fake-chat";
        public string EmbeddingModelId => "fake-embedding";

        public Task<ModelCompletionResult> CompleteAsync(ModelCompletionRequest request, CancellationToken ct)
        {
            Calls++;
            LastRequest = request;
            return Task.FromResult(new ModelCompletionResult(
                ResponseText,
                ChatModelId,
                FixedTier,
                false,
                TimeSpan.Zero));
        }

        public Task<ModelEmbeddingResult> EmbedAsync(ModelEmbeddingRequest request, CancellationToken ct) =>
            Task.FromResult(new ModelEmbeddingResult([], EmbeddingModelId, 0));

        public Task<ModelCapabilitySnapshot> ProbeAsync(CancellationToken ct) =>
            Task.FromResult(new ModelCapabilitySnapshot(true, true, ChatModelId, EmbeddingModelId, FixedTier, "ok"));
    }

    private sealed class FailingGateway : IModelGateway
    {
        public CapabilityState Capability => CapabilityState.Degraded(CapabilityKind.ModelGateway, "offline");
        public string ChatModelId => "offline";
        public string EmbeddingModelId => "offline";
        public Task<ModelCompletionResult> CompleteAsync(ModelCompletionRequest request, CancellationToken ct) =>
            Task.FromException<ModelCompletionResult>(new ModelGatewayException("endpoint offline"));
        public Task<ModelEmbeddingResult> EmbedAsync(ModelEmbeddingRequest request, CancellationToken ct) =>
            Task.FromException<ModelEmbeddingResult>(new ModelGatewayException("endpoint offline"));
        public Task<ModelCapabilitySnapshot> ProbeAsync(CancellationToken ct) =>
            Task.FromResult(new ModelCapabilitySnapshot(false, false, null, "offline", ModelEndpointTier.LocalOffline, "offline"));
    }

    private sealed class CapabilityHandler : HttpMessageHandler
    {
        private int _requestCount;
        public int RequestCount => _requestCount;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/embed")
            {
                return Json("""{"embeddings":[[0.1,0.2]]}""");
            }

            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            if (body.Contains("\"stream\":true", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"message":{"content":"OK"}}""" + "\n", Encoding.UTF8, "application/x-ndjson")
                };
            }

            if (body.Contains("\"tools\"", StringComparison.Ordinal))
            {
                return Json("""{"message":{"content":"","tool_calls":[{"function":{"name":"noop"}}]}}""");
            }

            if (body.Contains("\"format\":\"json\"", StringComparison.Ordinal))
            {
                return Json("""{"message":{"content":"{\"ok\":true}"}}""");
            }

            return Json("""{"message":{"content":"OK"}}""");
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
