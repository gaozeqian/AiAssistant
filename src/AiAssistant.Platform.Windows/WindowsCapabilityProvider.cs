using AiAssistant.Application.Ports;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Platform.Windows;

/// <summary>
/// Windows capability matrix. The wake-word row is supplied by the composition root because voice
/// readiness lives in the voice stack (models, ASR, TTS, playback), not in a platform adapter;
/// a hard-coded row would drift from what the voice page actually reports.
/// </summary>
public sealed class WindowsCapabilityProvider(
    IScreenCapture screenCapture,
    IWindowActivitySource windowActivitySource,
    IFileActivitySource fileActivitySource,
    ITextExtractor textExtractor,
    IKeyStore keyStore,
    IAutoStartService autoStartService,
    IAudioCaptureFactory? audioCaptureFactory = null,
    Func<CapabilityState?>? wakeWordCapability = null) : ICapabilityProvider
{
    public string PlatformName => "Windows";

    public IReadOnlyList<CapabilityState> GetCapabilities() =>
    [
        screenCapture.Capability,
        windowActivitySource.Capability,
        fileActivitySource.Capability,
        textExtractor.AccessibilityCapability,
        textExtractor.OcrCapability,
        keyStore.Capability,
        autoStartService.Capability,
        CapabilityState.Supported(CapabilityKind.SystemTray, "Avalonia 托盘已启用"),
        audioCaptureFactory?.Capability ?? CapabilityState.Unavailable(
            CapabilityKind.AudioCapture,
            "音频采集未接入当前运行时",
            "请重新启动应用；若问题持续，请导出诊断。"),
        wakeWordCapability?.Invoke() ?? CapabilityState.Degraded(
            CapabilityKind.WakeWord,
            "语音能力未接入当前运行时",
            "请重新启动应用；若问题持续，请导出诊断。")
    ];
}
