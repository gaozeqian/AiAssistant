using AiAssistant.Application.Ports;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Platform.Windows;

public sealed class WindowsCapabilityProvider(
    IScreenCapture screenCapture,
    IWindowActivitySource windowActivitySource,
    IFileActivitySource fileActivitySource,
    ITextExtractor textExtractor,
    IKeyStore keyStore,
    IAutoStartService autoStartService) : ICapabilityProvider
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
        CapabilityState.Supported(CapabilityKind.AudioCapture, "WASAPI 共享模式采集（AudioHub）"),
        CapabilityState.Degraded(
            CapabilityKind.WakeWord,
            "语音为可选功能，请在「语音」页开启",
            "需要在 AIASSISTANT_SHERPA_MODEL_DIR 下提供 sherpa-onnx KWS/TTS 模型。")
    ];
}
