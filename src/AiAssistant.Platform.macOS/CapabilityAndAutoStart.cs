using System.Globalization;
using System.Text;
using System.Xml.Linq;
using AiAssistant.Application.Ports;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Platform.macOS.Ipc;

namespace AiAssistant.Platform.macOS;

internal static class MacHelperCapabilityEvaluator
{
    public static CapabilityState EvaluateHelperCapability(
        IMacHelperSupervisor supervisor,
        CapabilityKind kind,
        MacPermissionGrant? requiredGrant,
        string supportedSummary,
        string? permissionRemediation = null,
        bool degradeInsteadOfRequirePermission = false)
    {
        return supervisor.Status switch
        {
            MacHelperSupervisorStatus.ProtocolIncompatible =>
                CapabilityState.Degraded(
                    kind,
                    supervisor.StatusSummary,
                    "请重新安装完整的 macOS 应用包，使 Host 与 Swift Helper 的 IPC 版本一致。"),
            MacHelperSupervisorStatus.Unavailable or MacHelperSupervisorStatus.Stopped =>
                CapabilityState.Unavailable(
                    kind,
                    supervisor.StatusSummary,
                    "请确保 aiassistant-mac-helper 已打包到 Contents/MacOS 并完成签名。"),
            MacHelperSupervisorStatus.Starting or MacHelperSupervisorStatus.BackoffRestarting =>
                CapabilityState.Degraded(
                    kind,
                    $"{supervisor.StatusSummary} (restarts={supervisor.RestartCount.ToString(CultureInfo.InvariantCulture)})",
                    "监督器正在以指数退避恢复 macOS Swift Helper。"),
            MacHelperSupervisorStatus.Healthy => requiredGrant switch
            {
                null or MacPermissionGrant.Granted => CapabilityState.Supported(kind, supportedSummary),
                MacPermissionGrant.Denied or MacPermissionGrant.NotDetermined when degradeInsteadOfRequirePermission =>
                    CapabilityState.Degraded(kind, $"{supportedSummary} (AX title restricted)", permissionRemediation),
                MacPermissionGrant.Denied or MacPermissionGrant.NotDetermined =>
                    CapabilityState.PermissionRequired(kind, $"{supportedSummary} awaiting TCC grant", permissionRemediation),
                _ => CapabilityState.Degraded(kind, $"{supportedSummary} (permission state unknown)", permissionRemediation)
            },
            _ => CapabilityState.Unavailable(kind, supervisor.StatusSummary)
        };
    }
}

/// <summary>
/// Dynamic macOS capability provider reflecting live Swift Helper supervision state, protocol compatibility,
/// and TCC permission grants (ADR-0005, implementation.md §3.5, S3).
/// </summary>
public sealed class CapabilityProvider : ICapabilityProvider
{
    private readonly IScreenCapture? _screenCapture;
    private readonly IWindowActivitySource? _windowActivitySource;
    private readonly IFileActivitySource? _fileActivitySource;
    private readonly ITextExtractor? _textExtractor;
    private readonly IAudioCapture? _audioCapture;
    private readonly IKeyStore _keyStore;
    private readonly IAutoStartService? _autoStartService;
    private readonly IMacHelperSupervisor? _supervisor;

    public CapabilityProvider(IKeyStore keyStore)
    {
        ArgumentNullException.ThrowIfNull(keyStore);
        _keyStore = keyStore;
    }

    public CapabilityProvider(
        IScreenCapture screenCapture,
        IWindowActivitySource windowActivitySource,
        IFileActivitySource fileActivitySource,
        ITextExtractor textExtractor,
        IAudioCapture audioCapture,
        IKeyStore keyStore,
        IAutoStartService autoStartService,
        IMacHelperSupervisor supervisor)
    {
        ArgumentNullException.ThrowIfNull(screenCapture);
        ArgumentNullException.ThrowIfNull(windowActivitySource);
        ArgumentNullException.ThrowIfNull(fileActivitySource);
        ArgumentNullException.ThrowIfNull(textExtractor);
        ArgumentNullException.ThrowIfNull(audioCapture);
        ArgumentNullException.ThrowIfNull(keyStore);
        ArgumentNullException.ThrowIfNull(autoStartService);
        ArgumentNullException.ThrowIfNull(supervisor);

        _screenCapture = screenCapture;
        _windowActivitySource = windowActivitySource;
        _fileActivitySource = fileActivitySource;
        _textExtractor = textExtractor;
        _audioCapture = audioCapture;
        _keyStore = keyStore;
        _autoStartService = autoStartService;
        _supervisor = supervisor;
    }

    public string PlatformName => "macOS";

    public IReadOnlyList<CapabilityState> GetCapabilities()
    {
        if (_screenCapture is null ||
            _windowActivitySource is null ||
            _fileActivitySource is null ||
            _textExtractor is null ||
            _audioCapture is null ||
            _autoStartService is null)
        {
            return
            [
                CapabilityState.PermissionRequired(CapabilityKind.ScreenCapture, "ScreenCaptureKit（Swift Helper）", "请在「系统设置 > 隐私与安全性」中授予「屏幕录制」。"),
                CapabilityState.PermissionRequired(CapabilityKind.WindowActivity, "NSWorkspace + AX（Swift Helper）", "请在「系统设置 > 隐私与安全性」中授予「辅助功能」。"),
                CapabilityState.Supported(CapabilityKind.FileActivity, "FSEvents 白名单目录监听"),
                _keyStore.Capability,
                CapabilityState.Supported(CapabilityKind.SystemTray, "Avalonia 托盘已启用"),
                CapabilityState.Supported(CapabilityKind.AutoStart, "launchd LaunchAgent 用户代理")
            ];
        }

        return
        [
            _screenCapture.Capability,
            _windowActivitySource.Capability,
            _fileActivitySource.Capability,
            _textExtractor.AccessibilityCapability,
            _textExtractor.OcrCapability,
            _audioCapture.Capability,
            _keyStore.Capability,
            _autoStartService.Capability,
            CapabilityState.Supported(CapabilityKind.SystemTray, "Avalonia NSStatusItem 托盘已启用"),
            CapabilityState.Degraded(
                CapabilityKind.WakeWord,
                "语音为可选功能，请在「语音」页开启",
                "需要在 AIASSISTANT_SHERPA_MODEL_DIR 下提供 sherpa-onnx KWS/TTS 模型。")
        ];
    }
}

/// <summary>
/// User-level macOS login item management via `~/Library/LaunchAgents/com.aiassistant.desktop.plist`.
/// Disabled by default; requires zero administrator privileges.
/// </summary>
public sealed class LaunchAgentAutoStartService : IAutoStartService
{
    public const string DefaultAgentLabel = "com.aiassistant.desktop";

    private readonly string _executablePath;
    private readonly string _agentLabel;
    private readonly string? _launchAgentsDirectory;

    public LaunchAgentAutoStartService(
        string executablePath,
        string agentLabel = DefaultAgentLabel,
        string? launchAgentsDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentLabel);

        _executablePath = executablePath;
        _agentLabel = agentLabel;
        _launchAgentsDirectory = launchAgentsDirectory ?? ResolveDefaultLaunchAgentsDirectory();
    }

    public string? PlistFilePath => _launchAgentsDirectory is null
        ? null
        : Path.Combine(_launchAgentsDirectory, $"{_agentLabel}.plist");

    public CapabilityState Capability => _launchAgentsDirectory is not null
        ? CapabilityState.Supported(CapabilityKind.AutoStart, $"launchd LaunchAgent ({_agentLabel}.plist)")
        : CapabilityState.Unavailable(CapabilityKind.AutoStart, "macOS LaunchAgent 仅在 macOS 主机可用");

    public Task<bool> IsEnabledAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (PlistFilePath is null || !File.Exists(PlistFilePath))
        {
            return Task.FromResult(false);
        }

        try
        {
            var doc = XDocument.Load(PlistFilePath);
            var hasRunAtLoad = doc.Descendants("key")
                .Any(k => string.Equals(k.Value, "RunAtLoad", StringComparison.Ordinal) &&
                          string.Equals((k.NextNode as XElement)?.Name.LocalName, "true", StringComparison.Ordinal));
            return Task.FromResult(hasRunAtLoad);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (PlistFilePath is null || _launchAgentsDirectory is null)
        {
            return;
        }

        if (!enabled)
        {
            if (File.Exists(PlistFilePath))
            {
                File.Delete(PlistFilePath);
            }

            return;
        }

        Directory.CreateDirectory(_launchAgentsDirectory);
        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist",
                new XAttribute("version", "1.0"),
                new XElement("dict",
                    new XElement("key", "Label"),
                    new XElement("string", _agentLabel),
                    new XElement("key", "ProgramArguments"),
                    new XElement("array",
                        new XElement("string", _executablePath)),
                    new XElement("key", "RunAtLoad"),
                    new XElement("true"),
                    new XElement("key", "KeepAlive"),
                    new XElement("false"))));

        var xml = doc.Declaration + Environment.NewLine + doc.ToString();
        await File.WriteAllTextAsync(PlistFilePath, xml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);
    }

    private static string? ResolveDefaultLaunchAgentsDirectory()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home) ? null : Path.Combine(home, "Library", "LaunchAgents");
    }
}
