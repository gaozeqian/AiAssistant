using AiAssistant.Application.Policies;
using AiAssistant.Application.Ports;
using AiAssistant.Application.UseCases;
using AiAssistant.Capture;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Desktop.ViewModels;
using AiAssistant.Platform.Linux;
using AiAssistant.Platform.Linux.Accessibility;
using AiAssistant.Platform.Linux.Desktop;
using AiAssistant.Platform.Linux.Portal;
using AiAssistant.Platform.Linux.X11;
using AiAssistant.Platform.MacOS;
using AiAssistant.Platform.MacOS.Ipc;
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

    public LinuxDisplayBackendState? LinuxBackendState { get; private set; }

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
        var linuxBackendState = OperatingSystem.IsLinux() ? new LinuxDisplayBackendState() : null;
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
        return new AppRuntime(services.BuildServiceProvider()) { LinuxBackendState = linuxBackendState };
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

        var macSupervisor = _services.GetService<IMacHelperSupervisor>();
        if (macSupervisor is not null)
        {
            await macSupervisor.StartAsync(ct);
        }

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
