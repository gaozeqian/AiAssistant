using AiAssistant.Application.Policies;
using AiAssistant.Application.Ports;
using AiAssistant.Application.UseCases;
using AiAssistant.Capture;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Desktop.ViewModels;
using AiAssistant.Export;
using AiAssistant.LocalApi;
using AiAssistant.Platform.Linux;
using AiAssistant.Platform.Linux.Accessibility;
using AiAssistant.Platform.Linux.Desktop;
using AiAssistant.Platform.Linux.Portal;
using AiAssistant.Platform.Linux.X11;
using AiAssistant.Platform.MacOS;
using AiAssistant.Platform.MacOS.Ipc;
using AiAssistant.Platform.Windows;
using AiAssistant.Processing;
using AiAssistant.Scheduler;
using AiAssistant.Storage;
using AiAssistant.Storage.KeyStores;
using AiAssistant.Storage.Media;
using AiAssistant.Storage.Observability;
using AiAssistant.Storage.Sqlite;
using AiAssistant.Storage.Vectors;
using AiAssistant.Summarization;
using AiAssistant.Models;
using AiAssistant.Voice;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AiAssistant.Host;

public sealed class AppRuntime : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly string _dataDirectory;
    private bool _started;

    private AppRuntime(ServiceProvider services, string dataDirectory)
    {
        _services = services;
        _dataDirectory = dataDirectory;
    }

    public int RecoveredJobCount { get; private set; }

    public LinuxDisplayBackendState? LinuxBackendState { get; private set; }

    public ILocalMcpControl McpControl => _services.GetRequiredService<ILocalMcpControl>();

    public IDatabaseMaintenanceService DatabaseMaintenance =>
        _services.GetRequiredService<IDatabaseMaintenanceService>();

    public IDataDeletionService DataDeletion => _services.GetRequiredService<IDataDeletionService>();

    public ModelBenchmarkRunner ModelBenchmark => _services.GetRequiredService<ModelBenchmarkRunner>();

    public IModelGateway ModelGateway => _services.GetRequiredService<IModelGateway>();

    public EventEmbeddingIndexer EmbeddingIndexer => _services.GetRequiredService<EventEmbeddingIndexer>();

    public IModelSecretAdmin ModelSecretAdmin => _services.GetRequiredService<IModelSecretAdmin>();

    public RangeReportUseCase RangeReport => _services.GetRequiredService<RangeReportUseCase>();

    public EvidenceSearchUseCase EvidenceSearch => _services.GetRequiredService<EvidenceSearchUseCase>();

    public IModelCapabilityProber ModelCapabilityProber =>
        _services.GetRequiredService<IModelCapabilityProber>();

    public static async Task<AppRuntime> CreateAsync(CancellationToken ct = default)
    {
        var dataDirectory = Environment.GetEnvironmentVariable("AIASSISTANT_DATA_DIRECTORY");
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AiAssistant");
        }

        dataDirectory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(dataDirectory);

        var whitelist = (Environment.GetEnvironmentVariable("AIASSISTANT_CAPTURE_DIRECTORIES") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var options = new StorageOptions { DataDirectory = dataDirectory };
        var clock = new SystemClock();
        var keyStore = new OsProtectedKeyStore(options);
        var connectionFactory = new EncryptedSqliteConnectionFactory(options, keyStore);
        var mediaStore = new AesGcmMediaStore(connectionFactory, keyStore);
        var eventRepository = new SqliteEventRepository(connectionFactory, mediaStore);
        var maintenance = new SqliteDatabaseMaintenanceService(connectionFactory);
        var jobQueue = new SqliteReportJobQueue(connectionFactory, clock);
        var structuredLogProvider = new SanitizedStructuredLoggerProvider(options, clock);
        var linuxBackendState = OperatingSystem.IsLinux() ? new LinuxDisplayBackendState() : null;
        var secretStore = new ProtectedModelSecretStore(Path.Combine(dataDirectory, "secrets"), keyStore);
        var modelsConfigPath = Environment.GetEnvironmentVariable("AIASSISTANT_MODELS_CONFIG");
        if (string.IsNullOrWhiteSpace(modelsConfigPath))
        {
            modelsConfigPath = Path.Combine(dataDirectory, "models.json");
        }

        var modelsConfiguration = await new ModelsConfigurationLoader(
                new ModelSecretResolver(secretStore))
            .LoadAsync(Path.GetFullPath(modelsConfigPath), ct);
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(structuredLogProvider);
        });
        services.AddSingleton<IClock>(clock);
        services.AddSingleton(options);
        services.AddSingleton<IDiagnosticLogReader>(structuredLogProvider);
        services.AddSingleton<IKeyStore>(keyStore);
        services.AddSingleton<IModelSecretAdmin>(new ModelSecretAdmin(secretStore));
        services.AddSingleton(connectionFactory);
        services.AddSingleton<IMediaStore>(mediaStore);
        services.AddSingleton(mediaStore);
        services.AddSingleton<IRetentionService>(provider => new MediaRetentionService(
            provider.GetRequiredService<IMediaStore>(),
            provider.GetRequiredService<IClock>(),
            provider.GetRequiredService<StorageOptions>(),
            provider.GetRequiredService<ILogger<MediaRetentionService>>()));
        services.AddSingleton<IEventRepository>(eventRepository);
        services.AddSingleton<IDataDeletionService>(provider => new DataDeletionService(
            connectionFactory,
            provider.GetRequiredService<IEventRepository>()));
        services.AddSingleton<IDatabaseMaintenanceService>(maintenance);
        services.AddSingleton<IReportJobQueue>(jobQueue);
        services.AddSingleton<IVectorIndex>(new HybridSqliteVectorIndex(connectionFactory));
        services.AddSingleton(secretStore);
        services.AddSingleton(modelsConfiguration);
        RegisterReports(services, modelsConfiguration, dataDirectory);
        services.AddSingleton(new CapturePipelineOptions { WhitelistedDirectories = whitelist });
        services.AddSingleton<PrivacyBlacklistPolicy>();
        if (linuxBackendState is not null)
        {
            services.AddSingleton(linuxBackendState);
        }

        if (OperatingSystem.IsWindows())
        {
            RegisterWindows(services, clock, keyStore);
        }
        else if (OperatingSystem.IsMacOS())
        {
            RegisterMacOS(services, clock, keyStore);
        }
        else
        {
            RegisterLinux(services, clock, keyStore, options, linuxBackendState ?? new LinuxDisplayBackendState());
        }

        var sherpaModelDirectory = Environment.GetEnvironmentVariable("AIASSISTANT_SHERPA_MODEL_DIR");
        if (string.IsNullOrWhiteSpace(sherpaModelDirectory))
        {
            sherpaModelDirectory = Path.Combine(dataDirectory, "models", "sherpa");
        }

        services.AddSingleton(new SherpaOnnxOptions { ModelDirectory = Path.GetFullPath(sherpaModelDirectory) });
        services.AddSingleton<IAsrEngine, SherpaOnnxAsrEngine>();
        services.AddSingleton<IVadSegmenter, SherpaOnnxVadSegmenter>();
        services.AddSingleton<ISpeakerDiarizationEngine, SherpaOnnxDiarizationEngine>();
        services.AddSingleton<IEventEnrichmentPipeline>(provider => new ProcessingPipeline(
            [
                new PerceptualDeduplicationStage(),
                new TextExtractionStage(provider.GetRequiredService<ITextExtractor>()),
                new RedactionStage(),
                new AudioTranscriptionStage(provider.GetRequiredService<IAsrEngine>()),
                new SpeakerDiarizationStage(provider.GetRequiredService<ISpeakerDiarizationEngine>())
            ],
            provider.GetRequiredService<ILogger<ProcessingPipeline>>()));
        services.AddSingleton<IAudioHub>(provider => new AudioHub(
            provider.GetRequiredService<IAudioCaptureFactory>(),
            provider.GetRequiredService<ILogger<AudioHub>>()));
        RegisterVoice(services, dataDirectory, clock);

        services.AddSingleton<ICaptureCoordinator>(provider => new BoundedCaptureCoordinator(
            provider.GetServices<ICaptureSource>(),
            provider.GetRequiredService<ITextExtractor>(),
            provider.GetRequiredService<IEventRepository>(),
            provider.GetRequiredService<PrivacyBlacklistPolicy>(),
            provider.GetRequiredService<IClock>(),
            provider.GetRequiredService<CapturePipelineOptions>(),
            provider.GetRequiredService<ILogger<BoundedCaptureCoordinator>>(),
            provider.GetService<IScreenCapture>(),
            provider.GetService<IWindowActivitySource>(),
            provider.GetService<IEventEnrichmentPipeline>()));
        services.AddSingleton<TimelineUseCase>();
        services.AddSingleton<CaptureControlUseCase>();
        services.AddSingleton<DiagnosticsUseCase>();
        services.AddSingleton<ReportUseCase>();
        RegisterSearchAndLocalApi(services, dataDirectory, keyStore, modelsConfiguration);
        return new AppRuntime(services.BuildServiceProvider(), dataDirectory) { LinuxBackendState = linuxBackendState };
    }

    private static void RegisterSearchAndLocalApi(
        IServiceCollection services,
        string dataDirectory,
        IKeyStore keyStore,
        ModelsConfiguration configuration)
    {
        services.AddSingleton<IRangeReportService>(provider => new ActivityRangeReportService(
            provider.GetRequiredService<IEventRepository>(),
            provider.GetRequiredService<IModelGateway>(),
            provider.GetRequiredService<ITextRedactor>(),
            provider.GetRequiredService<ILogger<ActivityRangeReportService>>()));
        services.AddSingleton<IReportRasterizer, SkiaReportRasterizer>();
        services.AddSingleton<RangeReportUseCase>();
        var configuredEmbeddingModel = configuration.Endpoints
            .FirstOrDefault(endpoint => endpoint.Tier == ModelEndpointTier.LocalOffline)
            ?.EmbeddingModel ?? string.Empty;
        var queryPrefix = EmbeddingProfile.ResolveQueryPrefix(
            configuredEmbeddingModel,
            Environment.GetEnvironmentVariable("AIASSISTANT_EMBEDDING_QUERY_PREFIX"));

        services.AddSingleton(new HybridSearchOptions { EmbeddingQueryPrefix = queryPrefix });
        services.AddSingleton<IEvidenceSearch>(provider => new HybridSearchService(
            provider.GetRequiredService<IEventRepository>(),
            provider.GetRequiredService<IVectorIndex>(),
            provider.GetRequiredService<IModelGateway>(),
            provider.GetRequiredService<HybridSearchOptions>(),
            provider.GetRequiredService<ILogger<HybridSearchService>>()));
        services.AddSingleton<IEvidenceQuestionAnswerer>(provider => new EvidenceQuestionAnswerUseCase(
            provider.GetRequiredService<IEvidenceSearch>(),
            provider.GetRequiredService<IModelGateway>(),
            provider.GetRequiredService<ILogger<EvidenceQuestionAnswerUseCase>>()));
        services.AddSingleton<EvidenceSearchUseCase>();

        services.AddSingleton(CreateMcpOptions());
        services.AddSingleton(new LocalMcpTokenStore(Path.Combine(dataDirectory, "security"), keyStore));
        services.AddSingleton(provider => new McpToolCatalog(
            provider.GetRequiredService<IEventRepository>(),
            provider.GetRequiredService<IReportRepository>(),
            provider.GetRequiredService<IClock>()));
        services.AddSingleton<LoopbackMcpServer>();
        services.AddSingleton<ILocalMcpControl, LocalMcpControl>();
    }

    private static McpServerOptions CreateMcpOptions() => new()
    {
        Enabled = string.Equals(
            Environment.GetEnvironmentVariable("AIASSISTANT_MCP_ENABLED"),
            "true",
            StringComparison.OrdinalIgnoreCase),
        Port = ParseBoundedInt(Environment.GetEnvironmentVariable("AIASSISTANT_MCP_PORT"), 47831, 0, 65535),
        MaxRequestsPerMinute = ParseBoundedInt(Environment.GetEnvironmentVariable("AIASSISTANT_MCP_RPM"), 60, 1, 10_000)
    };

    private static int ParseBoundedInt(string? raw, int fallback, int min, int max) =>
        int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, min, max)
            : fallback;

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_started)
        {
            return;
        }

        await _services.GetRequiredService<IDatabaseMaintenanceService>().InitializeAsync(ct);
        RecoveredJobCount = await _services.GetRequiredService<IReportJobQueue>().RecoverInterruptedJobsAsync(ct);
        await _services.GetRequiredService<IRetentionService>().StartAsync(ct);

        var macSupervisor = _services.GetService<IMacHelperSupervisor>();
        if (macSupervisor is not null)
        {
            await macSupervisor.StartAsync(ct);
        }

        using (var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            probeCts.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                var model = _services.GetRequiredService<IModelGateway>();
                _ = await model.ProbeAsync(probeCts.Token);
                var prober = _services.GetService<IModelCapabilityProber>();
                if (prober is not null)
                {
                    _ = await prober.ProbeAllAsync(forceRefresh: false, probeCts.Token);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _services.GetRequiredService<ILogger<AppRuntime>>().LogInformation(
                    "Local model probe timed out; report jobs will retry on demand.");
            }
        }

        await _services.GetRequiredService<ReportJobWorker>().StartAsync(ct);
        await _services.GetRequiredService<LoopbackMcpServer>().StartAsync(ct);
        await _services.GetRequiredService<ICaptureCoordinator>().StartAsync(ct);
        _services.GetRequiredService<ILogger<AppRuntime>>().LogInformation(
            "Runtime initialized cleanly; recoveredJobs={RecoveredJobs}.",
            RecoveredJobCount);
        _started = true;
    }

    /// <summary>Headless MCP-only startup for client conformance checks; never starts capture, reports or the UI.</summary>
    public async Task StartMcpOnlyAsync(CancellationToken ct = default)
    {
        await _services.GetRequiredService<IDatabaseMaintenanceService>().InitializeAsync(ct);
        await _services.GetRequiredService<LoopbackMcpServer>().StartAsync(ct);
    }

    public ShellViewModel CreateShellViewModel() =>
        new(
            new TimelineViewModel(_services.GetRequiredService<TimelineUseCase>()),
            new CaptureControlViewModel(_services.GetRequiredService<CaptureControlUseCase>()),
            new DiagnosticsViewModel(
                _services.GetRequiredService<DiagnosticsUseCase>(),
                _services.GetRequiredService<ILocalMcpControl>(),
                Path.Combine(_dataDirectory, "reports"),
                _services.GetRequiredService<IModelSecretAdmin>()),
            new ReportsViewModel(
                _services.GetRequiredService<ReportUseCase>(),
                Path.Combine(_dataDirectory, "reports")),
            new VoiceControlViewModel(_services.GetRequiredService<IVoiceSession>()),
            _services.GetRequiredService<VoiceNavigationHub>(),
            new SearchViewModel(
                _services.GetRequiredService<EvidenceSearchUseCase>(),
                _services.GetRequiredService<RangeReportUseCase>(),
                Path.Combine(_dataDirectory, "reports")),
            new DataManagementViewModel(_services.GetRequiredService<IDataDeletionService>()));

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
    }

    private static void RegisterVoice(IServiceCollection services, string dataDirectory, IClock clock)
    {
        var modelDirectory = Environment.GetEnvironmentVariable("AIASSISTANT_SHERPA_MODEL_DIR");
        if (string.IsNullOrWhiteSpace(modelDirectory))
        {
            modelDirectory = Path.Combine(dataDirectory, "models", "sherpa");
        }

        var voiceOptions = new SherpaVoiceOptions { ModelDirectory = Path.GetFullPath(modelDirectory) };
        var sessionOptions = new VoiceSessionOptions
        {
            EnableWakeChime = !string.Equals(
                Environment.GetEnvironmentVariable("AIASSISTANT_VOICE_DISABLE_CHIME"),
                "true",
                StringComparison.OrdinalIgnoreCase)
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
            provider.GetRequiredService<ILogger<VoiceSessionController>>()));
    }

    private static void RegisterReports(
        IServiceCollection services,
        ModelsConfiguration configuration,
        string dataDirectory)
    {
        var auditSink = new InMemoryModelAuditSink();
        var redactor = new SensitiveDataRedactor();
        var consent = new PublicCloudConsentGate();
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

        var router = new TierRoutingModelGateway(
            endpointGateways,
            configuration.Policy,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TierRoutingModelGateway>.Instance);
        var gateway = new AuditedModelGateway(
            new PolicyEnforcedModelGateway(
                new ThreeGateModelGateway(router, redactor),
                configuration.Policy,
                consent),
            auditSink);
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
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ModelCapabilityProber>.Instance));

        services.AddSingleton<IReportRepository, SqliteReportRepository>();
        services.AddSingleton(new ReportGenerationOptions { DefaultTier = configuration.Policy.DefaultTier });
        services.AddSingleton<IReportGenerationRunner, ReportGenerationRunner>();
        var localEmbeddingModel = configuration.Endpoints
            .FirstOrDefault(endpoint => endpoint.Tier == ModelEndpointTier.LocalOffline)
            ?.EmbeddingModel ?? string.Empty;
        var documentStyle = EmbeddingProfile.ResolveDocumentStyle(
            localEmbeddingModel,
            Environment.GetEnvironmentVariable("AIASSISTANT_EMBEDDING_DOCUMENT_STYLE"));
        services.AddSingleton(provider => new EventEmbeddingIndexer(
            provider.GetRequiredService<IEventRepository>(),
            provider.GetRequiredService<IVectorIndex>(),
            gateway,
            configuration.Policy.DefaultTier,
            documentStyle));
        services.AddSingleton(provider => new ModelBenchmarkRunner(
            provider.GetRequiredService<IModelGateway>(),
            configuration.Policy.DefaultTier));

        var scheduleTime = TimeOnly.TryParse(
            Environment.GetEnvironmentVariable("AIASSISTANT_REPORT_TIME"),
            out var parsedTime)
            ? parsedTime
            : new TimeOnly(18, 30);
        services.AddSingleton(new ReportScheduleOptions
        {
            DailyLocalTime = scheduleTime,
            EnableScheduledDaily = !string.Equals(
                Environment.GetEnvironmentVariable("AIASSISTANT_REPORT_DISABLE_SCHEDULE"),
                "true",
                StringComparison.OrdinalIgnoreCase),
            EnableStartupCatchUp = true
        });
        services.AddSingleton<ReportJobWorker>();
    }

    private static void RegisterWindows(IServiceCollection services, IClock clock, IKeyStore keyStore)
    {
        var screenCapture = new WindowsScreenCapture(clock);
        var windowSource = new WindowsWindowActivitySource(clock, screenCapture);
        var fileSource = new WhitelistedFileActivitySource(clock);
        var textExtractor = new WindowsTextExtractor();
        var processPath = Environment.ProcessPath;
        var executableCommand = string.IsNullOrWhiteSpace(processPath) ? "AiAssistant.Host" : $"\"{processPath}\"";
        var autoStart = new WindowsAutoStartService(executableCommand);

        services.AddSingleton<IScreenCapture>(screenCapture);
        services.AddSingleton<IWindowActivitySource>(windowSource);
        services.AddSingleton<IFileActivitySource>(fileSource);
        services.AddSingleton<ICaptureSource>(windowSource);
        services.AddSingleton<ICaptureSource>(fileSource);
        services.AddSingleton<ITextExtractor>(textExtractor);
        services.AddSingleton<IAutoStartService>(autoStart);
        services.AddSingleton<IAudioCaptureFactory>(new WindowsAudioCaptureFactory(clock));
        services.AddSingleton<IAudioPlayback>(new WindowsAudioPlayback());
        services.AddSingleton<ICapabilityProvider>(new WindowsCapabilityProvider(
            screenCapture,
            windowSource,
            fileSource,
            textExtractor,
            keyStore,
            autoStart));
    }

    private static void RegisterMacOS(IServiceCollection services, IClock clock, IKeyStore keyStore)
    {
        var helperPath = ResolveMacHelperPath();
        IMacHelperProcessLauncher? launcher = helperPath is not null ? new SubprocessMacHelperLauncher(helperPath) : null;
        var helperOptions = new MacHelperOptions { HelperExecutablePath = helperPath };
        var processPath = Environment.ProcessPath;
        var executablePath = string.IsNullOrWhiteSpace(processPath) ? "/Applications/AiAssistant.app/Contents/MacOS/AiAssistant.Host" : processPath;
        var autoStart = new MacOsLaunchAgentAutoStartService(executablePath);

        services.AddSingleton(helperOptions);
        services.AddSingleton<IMacHelperSupervisor>(provider => new MacHelperSupervisor(
            launcher,
            helperOptions,
            provider.GetRequiredService<ILogger<MacHelperSupervisor>>()));
        services.AddSingleton<IScreenCapture>(provider =>
            new MacOsScreenCapture(provider.GetRequiredService<IMacHelperSupervisor>(), clock));
        services.AddSingleton<IWindowActivitySource>(provider =>
            new MacOsWindowActivitySource(
                provider.GetRequiredService<IMacHelperSupervisor>(),
                clock,
                provider.GetRequiredService<IScreenCapture>()));
        services.AddSingleton<IFileActivitySource>(provider =>
            new MacOsFileActivitySource(provider.GetRequiredService<IMacHelperSupervisor>(), clock));
        services.AddSingleton<ICaptureSource>(provider => provider.GetRequiredService<IWindowActivitySource>());
        services.AddSingleton<ICaptureSource>(provider => provider.GetRequiredService<IFileActivitySource>());
        services.AddSingleton<ITextExtractor>(provider =>
            new MacOsTextExtractor(provider.GetRequiredService<IMacHelperSupervisor>()));
        services.AddSingleton<IAudioCapture>(provider =>
            new MacOsAudioCapture(provider.GetRequiredService<IMacHelperSupervisor>(), clock));
        services.AddSingleton<IAudioCaptureFactory>(provider =>
            new MacOsAudioCaptureFactory(provider.GetRequiredService<IMacHelperSupervisor>(), clock));
        services.AddSingleton<IAudioPlayback>(new MacOsAudioPlayback());
        services.AddSingleton<IAutoStartService>(autoStart);
        services.AddSingleton<ICapabilityProvider>(provider => new MacOsCapabilityProvider(
            provider.GetRequiredService<IScreenCapture>(),
            provider.GetRequiredService<IWindowActivitySource>(),
            provider.GetRequiredService<IFileActivitySource>(),
            provider.GetRequiredService<ITextExtractor>(),
            provider.GetRequiredService<IAudioCapture>(),
            keyStore,
            autoStart,
            provider.GetRequiredService<IMacHelperSupervisor>()));
    }

    private static void RegisterLinux(
        IServiceCollection services,
        IClock clock,
        IKeyStore keyStore,
        StorageOptions options,
        LinuxDisplayBackendState backendState)
    {
        var processPath = Environment.ProcessPath;
        var executablePath = string.IsNullOrWhiteSpace(processPath) ? "/usr/bin/aiassistant-host" : processPath;
        var autoStart = new XdgAutoStartService(executablePath);

        services.AddSingleton(backendState);
        services.AddSingleton<IXdgPortalClient, TmdsXdgPortalClient>();
        services.AddSingleton<IPortalRestoreTokenStore>(
            new ProtectedPortalRestoreTokenStore(Path.Combine(options.DataDirectory, "portal"), keyStore));
        services.AddSingleton<XdgPortalSessionManager>(provider => new XdgPortalSessionManager(
            provider.GetRequiredService<IXdgPortalClient>(),
            provider.GetRequiredService<IPortalRestoreTokenStore>(),
            provider.GetRequiredService<ILogger<XdgPortalSessionManager>>()));
        services.AddSingleton<IAtSpiAccessibilityClient, TmdsAtSpiClient>();
        services.AddSingleton(new X11ScreenCapture(clock));
        services.AddSingleton(provider => new WaylandScreenCapture(
            provider.GetRequiredService<IXdgPortalClient>(),
            provider.GetRequiredService<XdgPortalSessionManager>(),
            clock,
            provider.GetRequiredService<ILogger<WaylandScreenCapture>>()));
        services.AddSingleton<LinuxScreenCapture>();
        services.AddSingleton<IScreenCapture>(provider => provider.GetRequiredService<LinuxScreenCapture>());
        services.AddSingleton<IWindowActivitySource>(provider => new LinuxWindowActivitySource(
            backendState,
            provider.GetRequiredService<IAtSpiAccessibilityClient>(),
            clock,
            provider.GetRequiredService<X11ScreenCapture>()));
        services.AddSingleton<IFileActivitySource>(new LinuxInotifyFileActivitySource(clock));
        services.AddSingleton<ICaptureSource>(provider => provider.GetRequiredService<IWindowActivitySource>());
        services.AddSingleton<ICaptureSource>(provider => provider.GetRequiredService<IFileActivitySource>());
        services.AddSingleton<ITextExtractor>(provider => new LinuxTextExtractor(provider.GetRequiredService<IAtSpiAccessibilityClient>()));
        services.AddSingleton<IAudioCapture>(new LinuxPulseAudioCapture(clock));
        services.AddSingleton<IAudioCaptureFactory>(new LinuxAudioCaptureFactory(clock));
        services.AddSingleton<IAudioPlayback>(new LinuxAudioPlayback());
        services.AddSingleton<IAutoStartService>(autoStart);
        services.AddSingleton<ICapabilityProvider>(provider => new LinuxCapabilityProvider(
            provider.GetRequiredService<IScreenCapture>(),
            provider.GetRequiredService<IWindowActivitySource>(),
            provider.GetRequiredService<IFileActivitySource>(),
            provider.GetRequiredService<ITextExtractor>(),
            provider.GetRequiredService<IAudioCapture>(),
            keyStore,
            autoStart,
            backendState));
    }

    private static string? ResolveMacHelperPath()
    {
        var overridePath = Environment.GetEnvironmentVariable("AIASSISTANT_MAC_HELPER_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return Path.GetFullPath(overridePath);
        }

        var baseDir = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDir, "aiassistant-mac-helper"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "Helpers", "aiassistant-mac-helper"))
        ];

        return candidates.FirstOrDefault(File.Exists);
    }
}
