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

        var endpointGateways = new Dictionary<ModelEndpointTier, IModelGateway>();
        foreach (var endpoint in configuration.Endpoints)
        {
            var client = ModelHttpClientFactory.Create(endpoint);
            services.AddSingleton(client);
            var backendOptions = new ModelGatewayOptions
            {
                Provider = endpoint.Provider,
                BaseAddress = endpoint.BaseAddress,
                ChatModel = endpoint.ChatModel,
                EmbeddingModel = endpoint.EmbeddingModel,
                Think = endpoint.Think,
                ThinkingLevel = endpoint.ThinkingLevel,
                ApiKey = endpoint.ApiKey,
                RequestTimeout = endpoint.Timeout,
                AllowRemoteEndpoint = true
            };
            var backend = new HttpModelBackend(client, backendOptions);
            services.AddSingleton(backend);
            endpointGateways[endpoint.Tier] = new EndpointModelGateway(
                backend,
                endpoint,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<EndpointModelGateway>.Instance);
        }

        var chatRouter = new TierRoutingModelGateway(endpointGateways, configuration.Policy);
        var chatGateway = new AuditedModelGateway(
            new PolicyEnforcedModelGateway(
                new ThreeGateModelGateway(chatRouter, redactor),
                configuration.Policy,
                consent),
            auditSink,
            clock);

        // Embedding may target an independent endpoint (different tier/address/model). When no
        // dedicated embedding endpoint is configured, reuse the default tier's endpoint.
        var defaultTierEndpoint = configuration.Endpoints.First(item => item.Tier == configuration.Policy.DefaultTier);
        var embeddingEndpoint = configuration.EmbeddingEndpoint ?? defaultTierEndpoint;
        var embeddingClient = ModelHttpClientFactory.Create(embeddingEndpoint);
        services.AddSingleton(embeddingClient);
        var embeddingBackend = new HttpModelBackend(embeddingClient, new ModelGatewayOptions
        {
            Provider = embeddingEndpoint.Provider,
            BaseAddress = embeddingEndpoint.BaseAddress,
            ChatModel = embeddingEndpoint.ChatModel,
            EmbeddingModel = embeddingEndpoint.EmbeddingModel,
            ApiKey = embeddingEndpoint.ApiKey,
            RequestTimeout = embeddingEndpoint.Timeout,
            AllowRemoteEndpoint = true
        });
        services.AddSingleton(embeddingBackend);
        var embeddingEndpointGateway = new EndpointModelGateway(
            embeddingBackend,
            embeddingEndpoint,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<EndpointModelGateway>.Instance);
        var embeddingGateway = new AuditedModelGateway(
            new PolicyEnforcedModelGateway(
                new ThreeGateModelGateway(embeddingEndpointGateway, redactor),
                configuration.Policy,
                consent),
            auditSink,
            clock);

        var gateway = new SplitChatEmbeddingGateway(chatGateway, embeddingGateway, embeddingEndpoint.Tier);
        services.AddSingleton(gateway);
        services.AddSingleton<IModelGateway>(gateway);
        var standardClient = new GatewayChatClient(gateway, configuration.Policy.DefaultTier);
        services.AddSingleton<IChatClient>(standardClient);
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(standardClient);

        var capabilityCache = new ModelCapabilityCache(Path.Combine(dataDirectory, "model-capabilities.json"));
        services.AddSingleton(capabilityCache);
        var probeEndpoints = configuration.Endpoints
            .Where(endpoint => endpoint.Tier switch
            {
                ModelEndpointTier.LocalOffline => true,
                ModelEndpointTier.CorporateIntranet => configuration.Policy.AllowIntranet,
                ModelEndpointTier.PublicCloud => configuration.Policy.AllowPublicCloud &&
                                                 (!configuration.Policy.RequirePublicCloudConsent || consent.IsGranted),
                _ => false
            })
            .ToArray();
        services.AddSingleton<IModelCapabilityProber>(new ModelCapabilityProber(
            probeEndpoints,
            capabilityCache,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ModelCapabilityProber>.Instance,
            clock: clock));

        services.AddSingleton<IReportRepository, SqliteReportRepository>();
        services.AddSingleton(new ReportGenerationOptions { DefaultTier = configuration.Policy.DefaultTier });
        services.AddSingleton<IReportGenerationRunner, ReportGenerationRunner>();
        var localEmbeddingModel = configuration.Endpoints
            .FirstOrDefault(endpoint => endpoint.Tier == ModelEndpointTier.LocalOffline)
            ?.EmbeddingModel ?? string.Empty;
        var documentStyle = EmbeddingProfile.ResolveDocumentStyle(
            localEmbeddingModel,
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

        var scheduleTime = TimeOnly.TryParse(
            Environment.GetEnvironmentVariable(EnvironmentNames.ReportTime),
            out var parsedTime)
            ? parsedTime
            : new TimeOnly(18, 30);
        services.AddSingleton(new ReportScheduleOptions
        {
            DailyLocalTime = scheduleTime,
            EnableScheduledDaily = !string.Equals(
                Environment.GetEnvironmentVariable(EnvironmentNames.ReportDisableSchedule),
                "true",
                StringComparison.OrdinalIgnoreCase),
            EnableStartupCatchUp = true
        });
        services.AddSingleton<ReportJobWorker>();
    }

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
