using System.Text;
using AiAssistant.Application.Ports;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Platform.Linux.Desktop;
using AiAssistant.Platform.Linux.Portal;

namespace AiAssistant.Platform.Linux;

/// <summary>Live Linux capability matrix reflecting backend fallback, portal permission state and compositor limitations.</summary>
public sealed class LinuxCapabilityProvider : ICapabilityProvider
{
    private readonly IScreenCapture? _screen;
    private readonly IWindowActivitySource? _window;
    private readonly IFileActivitySource? _file;
    private readonly ITextExtractor? _text;
    private readonly IAudioCapture? _audio;
    private readonly IKeyStore _keyStore;
    private readonly IAutoStartService? _autoStart;
    private readonly LinuxDisplayBackendState _backend;

    public LinuxCapabilityProvider(IKeyStore keyStore)
        : this(keyStore, new LinuxDisplayBackendState())
    {
    }

    public LinuxCapabilityProvider(IKeyStore keyStore, LinuxDisplayBackendState backend)
    {
        ArgumentNullException.ThrowIfNull(keyStore);
        ArgumentNullException.ThrowIfNull(backend);
        _keyStore = keyStore;
        _backend = backend;
    }

    public LinuxCapabilityProvider(
        IScreenCapture screen,
        IWindowActivitySource window,
        IFileActivitySource file,
        ITextExtractor text,
        IAudioCapture audio,
        IKeyStore keyStore,
        IAutoStartService autoStart,
        LinuxDisplayBackendState backend)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(keyStore);
        ArgumentNullException.ThrowIfNull(autoStart);
        ArgumentNullException.ThrowIfNull(backend);

        _screen = screen;
        _window = window;
        _file = file;
        _text = text;
        _audio = audio;
        _keyStore = keyStore;
        _autoStart = autoStart;
        _backend = backend;
    }

    public string PlatformName
    {
        get
        {
            var desktop = string.IsNullOrWhiteSpace(_backend.Session.Desktop) ? "unknown desktop" : _backend.Session.Desktop;
            return $"Linux ({_backend.Decision.Summary}; {desktop})";
        }
    }

    public IReadOnlyList<CapabilityState> GetCapabilities()
    {
        if (_screen is null || _window is null || _file is null || _text is null || _audio is null || _autoStart is null)
        {
            return
            [
                CapabilityState.Degraded(CapabilityKind.ScreenCapture, _backend.Decision.Summary),
                CapabilityState.Degraded(CapabilityKind.WindowActivity, _backend.Decision.Summary),
                CapabilityState.Supported(CapabilityKind.FileActivity, "inotify-backed whitelisted directories"),
                _keyStore.Capability,
                CapabilityState.Supported(CapabilityKind.AutoStart, "XDG autostart user entry")
            ];
        }

        return
        [
            _screen.Capability,
            _window.Capability,
            _file.Capability,
            _text.AccessibilityCapability,
            _text.OcrCapability,
            _audio.Capability,
            _keyStore.Capability,
            _autoStart.Capability,
            _backend.Decision.Backend == LinuxDisplayBackendKind.Wayland
                ? CapabilityState.Degraded(CapabilityKind.GlobalHotkey, "Wayland restricts global hotkeys; use tray or voice commands")
                : CapabilityState.Degraded(CapabilityKind.GlobalHotkey, "X11 global hotkeys are not registered in the minimal shell"),
            CapabilityState.Degraded(CapabilityKind.SystemTray, "Requires StatusNotifierItem/AppIndicator support from the desktop")
        ];
    }
}

/// <summary>
/// Optional XDG user autostart entry. Writes one application-owned .desktop file atomically,
/// without elevated privileges or touching unrelated desktop entries.
/// </summary>
public sealed class XdgAutoStartService : IAutoStartService
{
    private readonly string _executablePath;
    private readonly string? _autostartDirectory;

    public XdgAutoStartService(string executablePath, string? autostartDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _executablePath = executablePath;
        _autostartDirectory = autostartDirectory ?? ResolveDefaultDirectory();
    }

    public string? DesktopFilePath => _autostartDirectory is null ? null : Path.Combine(_autostartDirectory, "aiassistant.desktop");

    public CapabilityState Capability => _autostartDirectory is null
        ? CapabilityState.Unavailable(CapabilityKind.AutoStart, "XDG autostart is unavailable on this host")
        : CapabilityState.Supported(CapabilityKind.AutoStart, "XDG ~/.config/autostart user entry");

    public async Task<bool> IsEnabledAsync(CancellationToken ct)
    {
        if (DesktopFilePath is null || !File.Exists(DesktopFilePath))
        {
            return false;
        }

        try
        {
            var text = await File.ReadAllTextAsync(DesktopFilePath, ct);
            var lines = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in lines)
            {
                var separator = line.IndexOf('=');
                if (separator > 0 && !line.StartsWith('#'))
                {
                    values[line[..separator]] = line[(separator + 1)..];
                }
            }

            return values.TryGetValue("Type", out var type) && type == "Application" &&
                   values.ContainsKey("Exec") &&
                   (!values.TryGetValue("Hidden", out var hidden) || !string.Equals(hidden, "true", StringComparison.OrdinalIgnoreCase)) &&
                   (!values.TryGetValue("X-GNOME-Autostart-enabled", out var enabled) || !string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_autostartDirectory is null || DesktopFilePath is null)
        {
            return;
        }

        if (!enabled)
        {
            if (File.Exists(DesktopFilePath))
            {
                File.Delete(DesktopFilePath);
            }

            return;
        }

        Directory.CreateDirectory(_autostartDirectory);
        var content = $"""
            [Desktop Entry]
            Type=Application
            Version=1.0
            Name=AiAssistant
            Comment=Local encrypted activity timeline
            Exec={QuoteExec(_executablePath)}
            Terminal=false
            NoDisplay=true
            X-GNOME-Autostart-enabled=true
            X-GNOME-Autostart-Delay=2
            Hidden=false

            """;
        var temporaryPath = $"{DesktopFilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, new UTF8Encoding(false), ct);
            File.Move(temporaryPath, DesktopFilePath, overwrite: true);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(DesktopFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string QuoteExec(string path)
    {
        if (!path.Any(char.IsWhiteSpace))
        {
            return path;
        }

        return $"\"{path.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
    }

    private static string? ResolveDefaultDirectory()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home))
            {
                return null;
            }

            configHome = Path.Combine(home, ".config");
        }

        return Path.Combine(configHome, "autostart");
    }
}
