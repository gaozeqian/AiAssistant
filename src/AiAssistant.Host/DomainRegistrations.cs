using AiAssistant.Application.Diagnostics;
using AiAssistant.Application.Policies;
using AiAssistant.Application.Ports;
using AiAssistant.Application.UseCases;
using AiAssistant.Capture;
using AiAssistant.Core.Configuration;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Desktop.ViewModels;
using AiAssistant.Export;
using AiAssistant.LocalApi;
using AiAssistant.Models;
using AiAssistant.Platform.Linux;
using AiAssistant.Platform.Linux.Accessibility;
using AiAssistant.Platform.Linux.Desktop;
using AiAssistant.Platform.Linux.PipeWire;
using AiAssistant.Platform.Linux.Portal;
using AiAssistant.Platform.Linux.X11;
using AiAssistant.Platform.macOS;
using AiAssistant.Platform.macOS.Ipc;
using AiAssistant.Platform.Windows;
using AiAssistant.Processing;
using AiAssistant.Processing.Ocr;
using AiAssistant.Scheduler;
using AiAssistant.Storage;
using AiAssistant.Storage.KeyStores;
using AiAssistant.Storage.Media;
using AiAssistant.Storage.Observability;
using AiAssistant.Storage.Sqlite;
using AiAssistant.Storage.Vectors;
using AiAssistant.Summarization;
using AiAssistant.Voice;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AiAssistant.Host;

/// <summary>
/// Domain module registration: model paths, ASR/voice and report generation wiring.
/// </summary>
internal static class DomainRegistrations
{
    internal static string ResolveModelDirectory(string dataDirectory, string name, params string[] markers)
    {
        var dataPath = Path.GetFullPath(Path.Combine(dataDirectory, "models", name));
        if (HasModelMarker(dataPath, markers))
        {
            return dataPath;
        }

        var bundledPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "models", name));
        return HasModelMarker(bundledPath, markers) ? bundledPath : dataPath;
    }

    internal static string ResolveModelFile(string dataDirectory, string name, string fileName)
    {
        var dataPath = Path.GetFullPath(Path.Combine(dataDirectory, "models", name, fileName));
        if (File.Exists(dataPath))
        {
            return dataPath;
        }

        var bundledPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "models", name, fileName));
        return File.Exists(bundledPath) ? bundledPath : dataPath;
    }

    internal static bool HasModelMarker(string directory, string[] markers) =>
        markers.Length == 0
            ? Directory.Exists(directory)
            : markers.Any(marker => File.Exists(Path.Combine(directory, marker)));

    /// <summary>Whisper is opt-in for high-quality batch recognition; sherpa-onnx stays the default.</summary>
    internal static void RegisterAsrEngine(IServiceCollection services, string dataDirectory)
    {
        var kind = AsrEngineSelection.Parse(
            Environment.GetEnvironmentVariable(AsrEngineSelection.EnvironmentVariableName));
        if (kind != AsrEngineKind.Whisper)
        {
            services.AddSingleton<IAsrEngine, SherpaOnnxAsrEngine>();
            return;
        }

        var modelPath = Environment.GetEnvironmentVariable(EnvironmentNames.WhisperModelPath);
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            modelPath = ResolveModelFile(dataDirectory, "whisper", "ggml-small.bin");
        }

        services.AddSingleton(new WhisperAsrOptions { ModelPath = Path.GetFullPath(modelPath) });
        services.AddSingleton<IAsrEngine, WhisperAsrEngine>();
    }

    internal static void RegisterVoice(IServiceCollection services, string dataDirectory, IClock clock)
    {
        var modelDirectory = Environment.GetEnvironmentVariable(EnvironmentNames.SherpaModelDir);
        if (string.IsNullOrWhiteSpace(modelDirectory))
        {
            modelDirectory = ResolveModelDirectory(
                dataDirectory,
                "sherpa",
                Path.Combine("kws", "encoder.onnx"),
                "model.int8.onnx",
                Path.Combine("tts", "model.onnx"),
                "silero_vad.onnx");
        }

        // Fall back to the tuned defaults declared on SherpaVoiceOptions so the two cannot drift.
        var voiceDefaults = new SherpaVoiceOptions();
        var voiceOptions = new SherpaVoiceOptions
        {
            ModelDirectory = Path.GetFullPath(modelDirectory),
            KeywordsScore = ReadFloatEnvironment(EnvironmentNames.KwsScore, voiceDefaults.KeywordsScore, 0.1f, 10f),
            KeywordsThreshold = ReadFloatEnvironment(EnvironmentNames.KwsThreshold, voiceDefaults.KeywordsThreshold, 0.01f, 0.99f)
        };
        var sessionOptions = new VoiceSessionOptions
        {
            EnableWakeChime = !string.Equals(
                Environment.GetEnvironmentVariable(EnvironmentNames.VoiceDisableChime),
                "true",
                StringComparison.OrdinalIgnoreCase),
            RecordCommandAudio = string.Equals(
                Environment.GetEnvironmentVariable(EnvironmentNames.VoiceRecordCommandAudio),
                "true",
                StringComparison.OrdinalIgnoreCase),
            CommandAudioRetention = TimeSpan.FromDays(ParseBoundedInt(
                Environment.GetEnvironmentVariable(EnvironmentNames.VoiceAudioRetentionDays),
                7,
                1,
                365))
        };
        var navigation = new VoiceNavigationHub();
        services.AddSingleton(voiceOptions);
        services.AddSingleton(sessionOptions);
        services.AddSingleton(navigation);
        services.AddSingleton<IVoiceViewNavigator>(navigation);
        services.AddSingleton<IWakeWordDetector, SherpaOnnxWakeWordDetector>();
        services.AddSingleton<ITtsService, SherpaOnnxTtsService>();
        services.AddSingleton<IVoiceCommandLog, SqliteVoiceCommandLog>();
        services.AddSingleton<VoiceCommandRouter>();
        services.AddSingleton<IVoiceSession>(provider => new VoiceSessionController(
            provider.GetRequiredService<IAudioHub>(),
            provider.GetRequiredService<IWakeWordDetector>(),
            provider.GetRequiredService<IAsrEngine>(),
            provider.GetRequiredService<ITtsService>(),
            provider.GetRequiredService<IAudioPlayback>(),
            provider.GetRequiredService<VoiceCommandRouter>(),
            provider.GetRequiredService<IVoiceCommandLog>(),
            clock,
            sessionOptions,
            provider.GetRequiredService<ILogger<VoiceSessionController>>(),
            provider.GetRequiredService<IMediaStore>()));
    }

    internal static void RegisterReports(
        IServiceCollection services,
        ModelsConfiguration configuration,
        string dataDirectory,
        IClock clock)
    {
        var auditSink = new InMemoryModelAuditSink();
        var redactor = new SensitiveDataRedactor();
        var consent = new PublicCloudConsentGate(clock);
        services.AddSingleton(configuration.Policy);
        services.AddSingleton(consent);
        services.AddSingleton<IModelAuditSink>(auditSink);
        services.AddSingleton<ITextRedactor>(redactor);

        // Chat endpoint: a single active model, so the tier router holds exactly one gateway.
        var chatClient = ModelHttpClientFactory.Create(configuration.Chat);
        services.AddSingleton(chatClient);
        var chatBackend = new HttpModelBackend(chatClient, new ModelGatewayOptions
        {
            BaseAddress = configuration.Chat.BaseAddress,
            ChatModel = configuration.Chat.ChatModel,
            EmbeddingModel = configuration.Chat.EmbeddingModel,
            Think = configuration.Chat.Think,
            ThinkingLevel = configuration.Chat.ThinkingLevel,
            ApiKey = configuration.Chat.ApiKey,
            RequestTimeout = configuration.Chat.Timeout,
            AllowRemoteEndpoint = true
        });
        services.AddSingleton(chatBackend);
        var chatEndpointGateway = new EndpointModelGateway(
            chatBackend,
            configuration.Chat,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<EndpointModelGateway>.Instance);
        var chatRouter = new TierRoutingModelGateway(
            new Dictionary<ModelEndpointTier, IModelGateway> { [configuration.Chat.Tier] = chatEndpointGateway },
            configuration.Policy);
        var chatGateway = new AuditedModelGateway(
            new PolicyEnforcedModelGateway(
                new ThreeGateModelGateway(chatRouter, redactor),
                configuration.Policy,
                consent),
            auditSink,
            clock);

        // Embedding endpoint is fully independent: its own tier, address, model and secret.
        var embeddingClient = ModelHttpClientFactory.Create(configuration.Embedding);
        services.AddSingleton(embeddingClient);
        var embeddingBackend = new HttpModelBackend(embeddingClient, new ModelGatewayOptions
        {
            BaseAddress = configuration.Embedding.BaseAddress,
            ChatModel = configuration.Embedding.ChatModel,
            EmbeddingModel = configuration.Embedding.EmbeddingModel,
            ApiKey = configuration.Embedding.ApiKey,
            RequestTimeout = configuration.Embedding.Timeout,
            AllowRemoteEndpoint = true
        });
        services.AddSingleton(embeddingBackend);
        var embeddingEndpointGateway = new EndpointModelGateway(
            embeddingBackend,
            configuration.Embedding,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<EndpointModelGateway>.Instance);
        var embeddingGateway = new AuditedModelGateway(
            new PolicyEnforcedModelGateway(
                new ThreeGateModelGateway(embeddingEndpointGateway, redactor),
                configuration.Policy,
                consent),
            auditSink,
            clock);

        var gateway = new SplitChatEmbeddingGateway(chatGateway, embeddingGateway, configuration.Embedding.Tier);
        services.AddSingleton(gateway);
        services.AddSingleton<IModelGateway>(gateway);
        var standardClient = new GatewayChatClient(gateway, configuration.Policy.DefaultTier);
        services.AddSingleton<IChatClient>(standardClient);
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(standardClient);

        var capabilityCache = new ModelCapabilityCache(Path.Combine(dataDirectory, "model-capabilities.json"));
        services.AddSingleton(capabilityCache);
        var probeTargets = new List<ModelProbeTarget>();
        if (IsTierAllowed(configuration.Chat.Tier, configuration.Policy, consent))
        {
            probeTargets.Add(new ModelProbeTarget(ModelEndpointRole.Chat, configuration.Chat));
        }

        if (IsTierAllowed(configuration.Embedding.Tier, configuration.Policy, consent))
        {
            probeTargets.Add(new ModelProbeTarget(ModelEndpointRole.Embedding, configuration.Embedding));
        }

        services.AddSingleton<IModelCapabilityProber>(new ModelCapabilityProber(
            probeTargets,
            capabilityCache,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ModelCapabilityProber>.Instance,
            clock: clock));

        services.AddSingleton<IReportRepository, SqliteReportRepository>();
        services.AddSingleton(new ReportGenerationOptions { DefaultTier = configuration.Policy.DefaultTier });
        services.AddSingleton<IReportGenerationRunner, ReportGenerationRunner>();
        var documentStyle = EmbeddingProfile.ResolveDocumentStyle(
            configuration.Embedding.EmbeddingModel,
            Environment.GetEnvironmentVariable(EnvironmentNames.EmbeddingDocumentStyle));
        services.AddSingleton(provider => new EventEmbeddingIndexer(
            provider.GetRequiredService<IEventRepository>(),
            provider.GetRequiredService<IVectorIndex>(),
            gateway,
            configuration.Policy.DefaultTier,
            documentStyle));
        services.AddSingleton(provider => new ModelBenchmarkRunner(
            provider.GetRequiredService<IModelGateway>(),
            configuration.Policy.DefaultTier,
            clock));

        // UI-saved schedule wins; environment variables only seed the first launch.
        var scheduleSettingsStore = new ReportScheduleSettingsStore(dataDirectory);
        services.AddSingleton<IReportScheduleSettingsStore>(scheduleSettingsStore);
        services.AddSingleton(new ReportScheduleSettingsUseCase(scheduleSettingsStore));
        services.AddSingleton(new ReportScheduleOptions { EnableStartupCatchUp = true });
        services.AddSingleton<ReportJobWorker>();
    }

    private static bool IsTierAllowed(
        ModelEndpointTier tier,
        ModelPolicy policy,
        PublicCloudConsentGate consent) =>
        tier switch
        {
            ModelEndpointTier.LocalOffline => true,
            ModelEndpointTier.CorporateIntranet => policy.AllowIntranet,
            ModelEndpointTier.PublicCloud => policy.AllowPublicCloud &&
                                             (!policy.RequirePublicCloudConsent || consent.IsGranted),
            _ => false
        };

    internal static float ReadFloatEnvironment(string name, float fallback, float min, float max)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return float.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, min, max)
            : fallback;
    }

    internal static int ParseBoundedInt(string? raw, int fallback, int min, int max) =>
        int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, min, max)
            : fallback;

}
