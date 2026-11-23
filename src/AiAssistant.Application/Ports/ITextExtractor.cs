using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

/// <summary>
/// Unified text extraction port: accessibility tree preferred, OCR fallback (design.md §3.1, implementation.md S2.3).
/// </summary>
public interface ITextExtractor
{
    CapabilityState AccessibilityCapability { get; }
    CapabilityState OcrCapability { get; }
    Task<ExtractedTextResult> ExtractAsync(TextExtractionRequest request, CancellationToken ct);
}
