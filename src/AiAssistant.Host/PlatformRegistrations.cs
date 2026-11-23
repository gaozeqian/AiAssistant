using AiAssistant.Application.Ports;
using AiAssistant.Core.Configuration;
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
using AiAssistant.Storage;
using AiAssistant.Storage.KeyStores;
using AiAssistant.Storage.Media;
using AiAssistant.Storage.Sqlite;
using AiAssistant.Voice;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AiAssistant.Host;

/// <summary>
/// Platform-specific dependency registration. Kept out of AppRuntime so the composition root
/// only orchestrates registration order and lifecycle.
/// </summary>
internal static class PlatformRegistrations
{
    internal static void RegisterWindows(IServiceCollection services, IClock clock, IKeyStore keyStore)
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

    internal static void RegisterMacPlatform(IServiceCollection services, IClock clock, IKeyStore keyStore)
    {
        var helperPath = ResolveMacHelperPath();
        IMacHelperProcessLauncher? launcher = helperPath is not null ? new SubprocessMacHelperLauncher(helperPath) : null;
        var helperOptions = new MacHelperOptions { HelperExecutablePath = helperPath };
        var processPath = Environment.ProcessPath;
        var executablePath = string.IsNullOrWhiteSpace(processPath) ? "/Applications/AiAssistant.app/Contents/MacOS/AiAssistant.Host" : processPath;
        var autoStart = new LaunchAgentAutoStartService(executablePath);

        services.AddSingleton(helperOptions);
        services.AddSingleton<IMacHelperSupervisor>(provider => new MacHelperSupervisor(
            launcher,
            helperOptions,
            provider.GetRequiredService<ILogger<MacHelperSupervisor>>()));
        services.AddSingleton<IScreenCapture>(provider =>
            new ScreenCapture(provider.GetRequiredService<IMacHelperSupervisor>(), clock));
        services.AddSingleton<IWindowActivitySource>(provider =>
            new WindowActivitySource(
                provider.GetRequiredService<IMacHelperSupervisor>(),
                clock,
                provider.GetRequiredService<IScreenCapture>()));
        services.AddSingleton<IFileActivitySource>(provider =>
            new FileActivitySource(provider.GetRequiredService<IMacHelperSupervisor>(), clock));
        services.AddSingleton<ICaptureSource>(provider => provider.GetRequiredService<IWindowActivitySource>());
        services.AddSingleton<ICaptureSource>(provider => provider.GetRequiredService<IFileActivitySource>());
        services.AddSingleton<ITextExtractor>(provider =>
            new TextExtractor(provider.GetRequiredService<IMacHelperSupervisor>()));
        services.AddSingleton<IImageOcrRecognizer>(provider =>
            new ImageOcrRecognizer(provider.GetRequiredService<IMacHelperSupervisor>()));
        services.AddSingleton<IAudioCapture>(provider =>
            new AudioCapture(provider.GetRequiredService<IMacHelperSupervisor>(), clock));
        services.AddSingleton<IAudioCaptureFactory>(provider =>
            new AudioCaptureFactory(provider.GetRequiredService<IMacHelperSupervisor>(), clock));
        services.AddSingleton<IAudioPlayback>(provider =>
            new AudioPlayback(provider.GetRequiredService<IMacHelperSupervisor>()));
        services.AddSingleton<IAutoStartService>(autoStart);
        services.AddSingleton<ICapabilityProvider>(provider => new CapabilityProvider(
            provider.GetRequiredService<IScreenCapture>(),
            provider.GetRequiredService<IWindowActivitySource>(),
            provider.GetRequiredService<IFileActivitySource>(),
            provider.GetRequiredService<ITextExtractor>(),
            provider.GetRequiredService<IAudioCapture>(),
            keyStore,
            autoStart,
            provider.GetRequiredService<IMacHelperSupervisor>()));
    }

    internal static void RegisterLinux(
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
        var pipeWireOptions = new WaylandPipeWireOptions
        {
            EnablePipeWire = !string.Equals(
                Environment.GetEnvironmentVariable(EnvironmentNames.WaylandPipeWire),
                "false",
                StringComparison.OrdinalIgnoreCase)
        };
        services.AddSingleton(pipeWireOptions);
        services.AddSingleton(provider => new PipeWireScreenCastStream(
            provider.GetRequiredService<ILogger<PipeWireScreenCastStream>>(),
            pipeWireOptions.MinFrameInterval,
            clock));
        services.AddSingleton(provider => new WaylandScreenCapture(
            provider.GetRequiredService<IXdgPortalClient>(),
            provider.GetRequiredService<XdgPortalSessionManager>(),
            clock,
            provider.GetRequiredService<ILogger<WaylandScreenCapture>>(),
            provider.GetRequiredService<PipeWireScreenCastStream>(),
            pipeWireOptions));
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
        var rapidOcrDirectory = Environment.GetEnvironmentVariable(EnvironmentNames.RapidOcrModelDir);
        if (string.IsNullOrWhiteSpace(rapidOcrDirectory))
        {
            rapidOcrDirectory = DomainRegistrations.ResolveModelDirectory(
                options.DataDirectory,
                "rapidocr",
                "ch_PP-OCRv4_det_infer.onnx",
                "ch_PP-OCRv4_rec_infer.onnx");
        }

        services.AddSingleton(new RapidOcrOptions { ModelDirectory = Path.GetFullPath(rapidOcrDirectory) });
        services.AddSingleton<RapidOcrTextRecognizer>();
        services.AddSingleton<IImageOcrRecognizer>(provider => provider.GetRequiredService<RapidOcrTextRecognizer>());
        services.AddSingleton<ITextExtractor>(provider => new LinuxTextExtractor(
            provider.GetRequiredService<IAtSpiAccessibilityClient>(),
            provider.GetRequiredService<IImageOcrRecognizer>()));
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

    internal static string? ResolveMacHelperPath()
    {
        var overridePath = Environment.GetEnvironmentVariable(EnvironmentNames.MacHelperPath);
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
