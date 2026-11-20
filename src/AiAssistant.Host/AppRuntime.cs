using AiAssistant.Application.Policies;
using AiAssistant.Application.Ports;
using AiAssistant.Application.UseCases;
using AiAssistant.Capture;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Desktop.ViewModels;
using AiAssistant.Platform.Linux;
using AiAssistant.Platform.MacOS;
using AiAssistant.Platform.Windows;
using AiAssistant.Storage;
using AiAssistant.Storage.KeyStores;
using AiAssistant.Storage.Media;
using AiAssistant.Storage.Observability;
using AiAssistant.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AiAssistant.Host;

public sealed class AppRuntime : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private bool _started;

    private AppRuntime(ServiceProvider services)
    {
        _services = services;
    }

    public int RecoveredJobCount { get; private set; }

    public static AppRuntime Create()
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
        services.AddSingleton(connectionFactory);
        services.AddSingleton<IMediaStore>(mediaStore);
        services.AddSingleton(mediaStore);
        services.AddSingleton<IRetentionService>(provider => new MediaRetentionService(
            provider.GetRequiredService<IMediaStore>(),
            provider.GetRequiredService<IClock>(),
            provider.GetRequiredService<StorageOptions>(),
            provider.GetRequiredService<ILogger<MediaRetentionService>>()));
        services.AddSingleton<IEventRepository>(eventRepository);
        services.AddSingleton<IDatabaseMaintenanceService>(maintenance);
        services.AddSingleton<IReportJobQueue>(jobQueue);
        services.AddSingleton(new CapturePipelineOptions { WhitelistedDirectories = whitelist });
        services.AddSingleton<PrivacyBlacklistPolicy>();

        if (OperatingSystem.IsWindows())
        {
            RegisterWindows(services, clock, keyStore);
        }
        else
        {
            services.AddSingleton<ITextExtractor>(new UnavailableTextExtractor());
            if (OperatingSystem.IsMacOS())
            {
                services.AddSingleton<IAutoStartService>(new MacOsNoOpAutoStartService());
                services.AddSingleton<ICapabilityProvider>(new MacOsCapabilityProvider(keyStore));
            }
            else
            {
                services.AddSingleton<IAutoStartService>(new LinuxNoOpAutoStartService());
                services.AddSingleton<ICapabilityProvider>(new LinuxCapabilityProvider(keyStore));
            }
        }

        services.AddSingleton<ICaptureCoordinator>(provider => new BoundedCaptureCoordinator(
            provider.GetServices<ICaptureSource>(),
            provider.GetRequiredService<ITextExtractor>(),
            provider.GetRequiredService<IEventRepository>(),
            provider.GetRequiredService<PrivacyBlacklistPolicy>(),
            provider.GetRequiredService<IClock>(),
            provider.GetRequiredService<CapturePipelineOptions>(),
            provider.GetRequiredService<ILogger<BoundedCaptureCoordinator>>(),
            provider.GetService<IScreenCapture>(),
            provider.GetService<IWindowActivitySource>()));
        services.AddSingleton<TimelineUseCase>();
        services.AddSingleton<CaptureControlUseCase>();
        services.AddSingleton<DiagnosticsUseCase>();
        return new AppRuntime(services.BuildServiceProvider());
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_started)
        {
            return;
        }

        await _services.GetRequiredService<IDatabaseMaintenanceService>().InitializeAsync(ct);
        RecoveredJobCount = await _services.GetRequiredService<IReportJobQueue>().RecoverInterruptedJobsAsync(ct);
        await _services.GetRequiredService<IRetentionService>().StartAsync(ct);
        await _services.GetRequiredService<ICaptureCoordinator>().StartAsync(ct);
        _services.GetRequiredService<ILogger<AppRuntime>>().LogInformation(
            "Runtime initialized cleanly; recoveredJobs={RecoveredJobs}.",
            RecoveredJobCount);
        _started = true;
    }

    public ShellViewModel CreateShellViewModel() =>
        new(
            new TimelineViewModel(_services.GetRequiredService<TimelineUseCase>()),
            new CaptureControlViewModel(_services.GetRequiredService<CaptureControlUseCase>()),
            new DiagnosticsViewModel(_services.GetRequiredService<DiagnosticsUseCase>()));

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
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
        services.AddSingleton<ICapabilityProvider>(new WindowsCapabilityProvider(
            screenCapture,
            windowSource,
            fileSource,
            textExtractor,
            keyStore,
            autoStart));
    }

    private sealed class UnavailableTextExtractor : ITextExtractor
    {
        public CapabilityState AccessibilityCapability =>
            CapabilityState.Unavailable(CapabilityKind.AccessibilityText, "Platform adapter is scheduled for S3.");

        public CapabilityState OcrCapability =>
            CapabilityState.Unavailable(CapabilityKind.Ocr, "Platform adapter is scheduled for S3.");

        public Task<ExtractedTextResult> ExtractAsync(TextExtractionRequest request, CancellationToken ct) =>
            Task.FromResult(new ExtractedTextResult(string.Empty, TextExtractionMethod.None, 0));
    }
}
