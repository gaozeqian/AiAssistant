using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Platform.macOS.Ipc;

namespace AiAssistant.Platform.macOS;

/// <summary>
/// macOS Accessibility-first + Vision OCR fallback text extractor via Swift Helper IPC.
/// Helper strictly skips `AXSecureTextField` password controls.
/// </summary>
public sealed class TextExtractor(IMacHelperSupervisor supervisor) : ITextExtractor
{
    public CapabilityState AccessibilityCapability => MacHelperCapabilityEvaluator.EvaluateHelperCapability(
        supervisor,
        CapabilityKind.AccessibilityText,
        supervisor.Permissions.Accessibility,
        "macOS AXUIElement 树（排除安全输入框）",
        "请在「系统设置 > 隐私与安全性 > 辅助功能」中启用 AiAssistant。");

    public CapabilityState OcrCapability => MacHelperCapabilityEvaluator.EvaluateHelperCapability(
        supervisor,
        CapabilityKind.Ocr,
        requiredGrant: null,
        "macOS Vision 文字识别（简繁中文/英文）");

    public async Task<ExtractedTextResult> ExtractAsync(TextExtractionRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var pngBase64 = request.ScreenshotPngBytes is { Length: > 0 } bytes
            ? Convert.ToBase64String(bytes)
            : null;

        var response = await supervisor.InvokeAsync(
            new MacIpcRequest(
                string.Empty,
                MacHelperProtocol.Methods.ExtractText,
                WindowId: request.WindowHandle,
                PngBase64: pngBase64),
            ct);

        if (response.Ok && response.ExtractedText is not null && !string.IsNullOrWhiteSpace(response.ExtractedText.Text))
        {
            var method = string.Equals(response.ExtractedText.Source, "accessibility", StringComparison.OrdinalIgnoreCase)
                ? TextExtractionMethod.AccessibilityTree
                : string.Equals(response.ExtractedText.Source, "ocr", StringComparison.OrdinalIgnoreCase)
                    ? TextExtractionMethod.SystemOcr
                    : TextExtractionMethod.None;

            return new ExtractedTextResult(
                response.ExtractedText.Text.Trim(),
                method,
                Math.Clamp(response.ExtractedText.Confidence, 0.0, 1.0));
        }

        var fallback = string.IsNullOrWhiteSpace(request.WindowTitle)
            ? request.AppName
            : $"{request.AppName} - {request.WindowTitle}";

        return new ExtractedTextResult(fallback, TextExtractionMethod.None, 0.50);
    }
}
