using AiAssistant.Application.Imaging;
using AiAssistant.Application.Ports;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Platform.Windows;

/// <summary>
/// Accessibility-first text extractor: inspects Win32 accessible child controls (strictly skipping ES_PASSWORD controls)
/// and falls back to the local OCR recognizer when accessibility yields no text (implementation.md S2.3).
/// </summary>
public sealed class WindowsTextExtractor(IImageOcrRecognizer? ocr = null) : ITextExtractor
{
    public CapabilityState AccessibilityCapability => OperatingSystem.IsWindows()
        ? CapabilityState.Supported(CapabilityKind.AccessibilityText, "Win32 UI 控件树（排除密码框）")
        : CapabilityState.Unavailable(CapabilityKind.AccessibilityText, "Windows 无障碍仅在 Windows 主机可用");

    /// <summary>
    /// Reports the recognizer's own state: assets present means OCR really works, missing assets
    /// means the row must ask for them instead of claiming a fallback that was never wired.
    /// </summary>
    public CapabilityState OcrCapability => ocr is not null
        ? ocr.Capability
        : CapabilityState.Unavailable(
            CapabilityKind.Ocr,
            "未接入图像 OCR 兜底（无障碍文本优先可用）",
            "请重新启动应用后点击「重新检查」。");

    public async Task<ExtractedTextResult> ExtractAsync(TextExtractionRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (OperatingSystem.IsWindows() && request.WindowHandle != 0)
        {
            var accessibleText = ExtractAccessibleChildTexts(request.WindowHandle);
            if (!string.IsNullOrWhiteSpace(accessibleText))
            {
                return new ExtractedTextResult(accessibleText, TextExtractionMethod.AccessibilityTree, 0.95);
            }
        }

        if (ocr is not null &&
            request.ScreenshotPngBytes is { Length: > 0 } png &&
            PngCodec.TryDecodeToBgra32(png, out var bgra, out var width, out var height, out _) &&
            bgra is not null)
        {
            var ocrText = await ocr.RecognizeAsync(new ImageFrameBuffer(bgra, width, height), ct);
            if (!string.IsNullOrWhiteSpace(ocrText))
            {
                return new ExtractedTextResult(ocrText.Trim(), TextExtractionMethod.SystemOcr, 0.80);
            }
        }

        var fallbackTitle = string.IsNullOrWhiteSpace(request.WindowTitle)
            ? request.AppName
            : $"{request.AppName} - {request.WindowTitle}";

        return new ExtractedTextResult(fallbackTitle, TextExtractionMethod.None, 0.50);
    }

    private static string ExtractAccessibleChildTexts(nint rootHwnd)
    {
        var parts = new List<string>();
        var totalChars = 0;

        _ = Win32Native.EnumChildWindows(rootHwnd, (childHwnd, _) =>
        {
            if (totalChars >= 2048)
            {
                return false;
            }

            var style = Win32Native.GetWindowLongW(childHwnd, Win32Native.GwlStyle);
            if ((style & Win32Native.EsPassword) != 0)
            {
                // Privacy boundary: never read password controls!
                return true;
            }

            if (Win32Native.SendMessageTimeoutW(
                    childHwnd,
                    Win32Native.WmGetTextLength,
                    0,
                    null,
                    Win32Native.SmtoAbortIfHung,
                    40,
                    out var lenResult) == 0)
            {
                return true;
            }

            var length = (int)Math.Min((ulong)lenResult, 512UL);
            if (length <= 1)
            {
                return true;
            }

            var buf = new char[length + 2];
            if (Win32Native.SendMessageTimeoutW(
                    childHwnd,
                    Win32Native.WmGetText,
                    (nuint)buf.Length,
                    buf,
                    Win32Native.SmtoAbortIfHung,
                    40,
                    out var copiedChars) != 0)
            {
                var actualLen = (int)Math.Min((ulong)copiedChars, (ulong)buf.Length);
                var text = new string(buf, 0, actualLen).TrimEnd('\0').Trim();
                if (text.Length > 1 && !parts.Contains(text, StringComparer.Ordinal))
                {
                    parts.Add(text);
                    totalChars += text.Length;
                }
            }

            return true;
        }, 0);

        return string.Join(" | ", parts);
    }
}
