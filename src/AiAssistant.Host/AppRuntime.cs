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

    /// <summary>Socket the running GUI listens on so a second launch can show its hidden window.</summary>
    public string InstanceSocketPath => Path.Combine(_dataDirectory, "instance.sock");

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

    /// <summary>Present only on macOS; used by --doctor to report helper health and TCC state.</summary>
    public IMacHelperSupervisor? MacHelperSupervisor => _services.GetService<IMacHelperSupervisor>();

    public ICapabilityProvider? Capabilities => _services.GetService<ICapabilityProvider>();

    /// <summary>
    /// True only when the platform reports a working system tray. Linux GNOME reports Degraded
    /// (needs StatusNotifierItem/AppIndicator), so there the window close must exit instead of hide.
    /// </summary>
    public bool IsSystemTraySupported => Capabilities?.GetCapabilities()
        .Any(capability => capability.Kind == CapabilityKind.SystemTray &&
                           capability.Status == CapabilityStatus.Supported) == true;

    public IAsrEngine AsrEngine => _services.GetRequiredService<IAsrEngine>();

    /// <summary>Streaming wake-word detector used by the KWS golden-set command.</summary>
    public IWakeWordDetector WakeWordDetector => _services.GetRequiredService<IWakeWordDetector>();

    /// <summary>Only registered where a managed image OCR adapter exists (Linux RapidOCR).</summary>
    public IImageOcrRecognizer? OcrRecognizer => _services.GetService<IImageOcrRecognizer>();

    /// <summary>Starts the macOS Swift helper for commands that need it without starting capture.</summary>
    public async Task StartMacHelperAsync(CancellationToken ct)
    {
        if (MacHelperSupervisor is { } supervisor)
        {
            await supervisor.StartAsync(ct);
        }
    }

    public IModelCapabilityProber ModelCapabilityProber =>
        _services.GetRequiredService<IModelCapabilityProber>();

    public static async Task<AppRuntime> CreateAsync(CancellationToken ct = default)
    {
        var dataDirectory = Environment.GetEnvironmentVariable(EnvironmentNames.DataDirectory);
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AiAssistant");
        }

        dataDirectory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(dataDirectory);

        var whitelist = (Environment.GetEnvironmentVariable(EnvironmentNames.CaptureDirectories) ?? string.Empty)
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
        var modelsConfigPath = Environment.GetEnvironmentVariable(EnvironmentNames.ModelsConfig);
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
        services.AddSingleton<IModelConfigurationAdmin>(new ModelConfigurationAdmin(
            Path.GetFullPath(modelsConfigPath),
            modelsConfiguration));
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
        DomainRegistrations.RegisterReports(services, modelsConfiguration, dataDirectory);
        services.AddSingleton(new CapturePipelineOptions { WhitelistedDirectories = whitelist });
        services.AddSingleton<PrivacyBlacklistPolicy>();
        if (linuxBackendState is not null)
        {
            services.AddSingleton(linuxBackendState);
        }

        if (OperatingSystem.IsWindows())
        {
            PlatformRegistrations.RegisterWindows(services, clock, keyStore);
        }
        else if (OperatingSystem.IsMacOS())
        {
            PlatformRegistrations.RegisterMacPlatform(services, clock, keyStore);
        }
        else
        {
            PlatformRegistrations.RegisterLinux(services, clock, keyStore, options, linuxBackendState ?? new LinuxDisplayBackendState());
        }

        var sherpaModelDirectory = Environment.GetEnvironmentVariable(EnvironmentNames.SherpaModelDir);
        if (string.IsNullOrWhiteSpace(sherpaModelDirectory))
        {
            sherpaModelDirectory = DomainRegistrations.ResolveModelDirectory(
                dataDirectory,
                "sherpa",
                Path.Combine("kws", "encoder.onnx"),
                "model.int8.onnx",
                Path.Combine("tts", "model.onnx"),
                "silero_vad.onnx");
        }

        services.AddSingleton(new SherpaOnnxOptions { ModelDirectory = Path.GetFullPath(sherpaModelDirectory) });
        DomainRegistrations.RegisterAsrEngine(services, dataDirectory);
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
        DomainRegistrations.RegisterVoice(services, dataDirectory, clock);

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
        var provider = services.BuildServiceProvider();
        SuppressedExceptionLog.Configure(provider.GetRequiredService<ILoggerFactory>());
        return new AppRuntime(provider, dataDirectory) { LinuxBackendState = linuxBackendState };
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
            provider.GetRequiredService<ILogger<ActivityRangeReportService>>(),
            configuration.Policy.DefaultTier));
        services.AddSingleton<IReportRasterizer, SkiaReportRasterizer>();
        services.AddSingleton<RangeReportUseCase>();
        var configuredEmbeddingModel = configuration.Endpoints
            .FirstOrDefault(endpoint => endpoint.Tier == ModelEndpointTier.LocalOffline)
            ?.EmbeddingModel ?? string.Empty;
        var queryPrefix = EmbeddingProfile.ResolveQueryPrefix(
            configuredEmbeddingModel,
            Environment.GetEnvironmentVariable(EnvironmentNames.EmbeddingQueryPrefix));

        services.AddSingleton(new HybridSearchOptions { EmbeddingQueryPrefix = queryPrefix });
        services.AddSingleton<IEvidenceSearch>(provider => new HybridSearchService(
            provider.GetRequiredService<IEventRepository>(),
            provider.GetRequiredService<IVectorIndex>(),
            provider.GetRequiredService<IModelGateway>(),
            provider.GetRequiredService<HybridSearchOptions>(),
            provider.GetRequiredService<ILogger<HybridSearchService>>(),
            ensureIndexed: async ct =>
            {
                var vectors = provider.GetRequiredService<IVectorIndex>();
                var metadata = await vectors.GetMetadataAsync(ct);
                if (metadata.IndexedCount > 0)
                {
                    return;
                }

                bool hasEvents = false;
                await foreach (var _ in provider.GetRequiredService<IEventRepository>()
                                   .QueryAsync(new EventQuery(Limit: 1), ct))
                {
                    hasEvents = true;
                    break;
                }

                if (hasEvents)
                {
                    await provider.GetRequiredService<EventEmbeddingIndexer>().RebuildAllAsync(ct);
                }
            },
            tier: configuration.Policy.DefaultTier));
        services.AddSingleton<IEvidenceQuestionAnswerer>(provider => new EvidenceQuestionAnswerUseCase(
            provider.GetRequiredService<IEvidenceSearch>(),
            provider.GetRequiredService<IModelGateway>(),
            provider.GetRequiredService<ILogger<EvidenceQuestionAnswerUseCase>>(),
            configuration.Policy.DefaultTier));
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
            Environment.GetEnvironmentVariable(EnvironmentNames.McpEnabled),
            "true",
            StringComparison.OrdinalIgnoreCase),
        Port = ParseBoundedInt(Environment.GetEnvironmentVariable(EnvironmentNames.McpPort), 47831, 0, 65535),
        MaxRequestsPerMinute = ParseBoundedInt(Environment.GetEnvironmentVariable(EnvironmentNames.McpRpm), 60, 1, 10_000)
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
                _services.GetRequiredService<IModelSecretAdmin>(),
                _services.GetRequiredService<IModelConfigurationAdmin>()),
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

    /// <summary>
    /// Resolves an optional model folder: the user data directory wins when it already contains
    /// model assets (so users can override a bundled model), otherwise the folder shipped next to
    /// the executable by the installer is used. Callers handle explicit environment overrides first.
    /// </summary>
}
