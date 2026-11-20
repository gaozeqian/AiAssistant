using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public enum TextExtractionMethod
{
    None = 0,
    AccessibilityTree = 1,
    SystemOcr = 2
}

public sealed record TextExtractionRequest(
    nint WindowHandle,
    string AppName,
    string WindowTitle,
    byte[]? ScreenshotPngBytes = null);

public sealed record ExtractedTextResult(
    string Text,
    TextExtractionMethod Method,
    double Confidence);

/// <summary>
/// Unified text extraction port: accessibility tree preferred, OCR fallback (DESIGN.md §3.1, IMPLEMENTATION.md S2.3).
/// </summary>
public interface ITextExtractor
{
    CapabilityState AccessibilityCapability { get; }
    CapabilityState OcrCapability { get; }
    Task<ExtractedTextResult> ExtractAsync(TextExtractionRequest request, CancellationToken ct);
}

/// <summary>
/// Composable preprocessing stage contract with explicit timeout &amp; cancellation (IMPLEMENTATION.md S4.1).
/// </summary>
public interface IProcessingStage<TInput, TOutput>
{
    string StageName { get; }
    TimeSpan DefaultTimeout { get; }
    Task<TOutput> ProcessAsync(TInput input, CancellationToken ct);
}
