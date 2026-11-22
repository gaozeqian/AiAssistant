using AiAssistant.Application.Imaging;
using AiAssistant.Application.Ports;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Platform.MacOS.Ipc;

namespace AiAssistant.Platform.MacOS;

/// <summary>
/// macOS Vision OCR over the Swift helper's versioned IPC (`text.extract` with PNG bytes and no
/// Accessibility target). Powers the OCR golden-set command and any image-only extraction path,
/// independent of the accessibility tree (S4-R2).
/// </summary>
public sealed class MacOsImageOcrRecognizer(IMacHelperSupervisor supervisor) : IImageOcrRecognizer
{
    public CapabilityState Capability => supervisor.Status == MacHelperSupervisorStatus.Healthy
        ? CapabilityState.Supported(CapabilityKind.Ocr, "Vision 文字识别（Swift Helper）")
        : CapabilityState.Degraded(
            CapabilityKind.Ocr,
            $"macOS helper {supervisor.Status}; Vision OCR unavailable",
            "请在诊断页检查 Helper 状态。");

    public async Task<string?> RecognizeAsync(ImageFrameBuffer frame, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Width <= 0 || frame.Height <= 0 ||
            frame.Bgra32Pixels.Length < (long)frame.Width * frame.Height * 4)
        {
            return null;
        }

        var png = PngCodec.EncodeBgra32(frame.Bgra32Pixels, frame.Width, frame.Height);
        var response = await supervisor.InvokeAsync(
            new MacIpcRequest(
                string.Empty,
                MacHelperProtocol.Methods.ExtractText,
                PngBase64: Convert.ToBase64String(png)),
            ct);

        if (!response.Ok)
        {
            if (string.Equals(
                    response.ErrorCode,
                    MacHelperProtocol.ErrorCodes.Disconnected,
                    StringComparison.OrdinalIgnoreCase))
            {
                // Helper restarting: degrade to "no text" instead of failing the caller.
                return null;
            }

            // Surface real IPC-level failures instead of pretending the image had no text.
            throw new InvalidOperationException(
                $"macOS Vision OCR failed: {response.ErrorCode ?? "unknown"} {response.ErrorMessage}".Trim());
        }

        if (response.ExtractedText is null)
        {
            return null;
        }

        var text = response.ExtractedText.Text;
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
