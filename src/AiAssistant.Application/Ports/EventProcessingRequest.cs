using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

/// <summary>
/// Best-effort enrichment pipeline. Implementations must return the original event when any stage fails;
/// raw capture persistence is never contingent on OCR, ASR or model availability.
/// </summary>
public sealed record EventProcessingRequest(
    ActivityEvent Event,
    byte[]? ScreenshotPng = null,
    AudioFrame? Audio = null,
    nint NativeWindowHandle = 0);
