using AiAssistant.Core.Domain;
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

/// <summary>Decoded BGRA32 frame handed to the OCR pipeline; decoding stays in the platform adapter.</summary>
public sealed record ImageFrameBuffer(byte[] Bgra32Pixels, int Width, int Height);

/// <summary>
/// Image-to-text OCR fallback used when the accessibility tree yields nothing.
/// Implementations stay replaceable; missing model assets only degrade OCR, never text capture.
/// </summary>
public interface IImageOcrRecognizer
{
    CapabilityState Capability { get; }

    Task<string?> RecognizeAsync(ImageFrameBuffer frame, CancellationToken ct);
}

/// <summary>
/// Composable preprocessing stage contract with explicit timeout &amp; cancellation (IMPLEMENTATION.md S4.1).
/// </summary>
public interface IProcessingStage<TInput, TOutput>
{
    string StageName { get; }
    string StageVersion { get; }
    TimeSpan DefaultTimeout { get; }

    /// <summary>Declared resource claims such as "ocr", "asr" or "cpu" used for scheduling and diagnostics.</summary>
    IReadOnlyCollection<string> ResourceClaims { get; }

    Task<TOutput> ProcessAsync(TInput input, CancellationToken ct);
}

public sealed record AudioTranscriptionRequest(
    ReadOnlyMemory<float> Samples,
    int SampleRateHz,
    string Language = "zh");

public sealed record AudioTranscriptionResult(
    string Text,
    double Confidence,
    string ModelVersion)
{
    public static readonly AudioTranscriptionResult Empty = new(string.Empty, 0, "unavailable");
}

/// <summary>Replaceable offline ASR engine (sherpa-onnx default, Whisper optional).</summary>
public interface IAsrEngine : IAsyncDisposable
{
    CapabilityState Capability { get; }
    Task<AudioTranscriptionResult> TranscribeAsync(AudioTranscriptionRequest request, CancellationToken ct);
}

public sealed record VadSegment(
    TimeSpan Start,
    TimeSpan End,
    ReadOnlyMemory<float> Samples);

/// <summary>Voice activity segmentation over the shared AudioHub stream.</summary>
public interface IVadSegmenter : IDisposable
{
    CapabilityState Capability { get; }
    IReadOnlyList<VadSegment> Accept(AudioFrame frame);
    IReadOnlyList<VadSegment> Flush();
}

public sealed record SpeakerTurn(
    string SpeakerId,
    TimeSpan Start,
    TimeSpan End);

public interface ISpeakerDiarizationEngine : IAsyncDisposable
{
    CapabilityState Capability { get; }
    string ModelVersion { get; }
    Task<IReadOnlyList<SpeakerTurn>> DiarizeAsync(
        ReadOnlyMemory<float> samples,
        int sampleRateHz,
        CancellationToken ct);
}

/// <summary>
/// Best-effort enrichment pipeline. Implementations must return the original event when any stage fails;
/// raw capture persistence is never contingent on OCR, ASR or model availability.
/// </summary>
public sealed record EventProcessingRequest(
    ActivityEvent Event,
    byte[]? ScreenshotPng = null,
    AudioFrame? Audio = null,
    nint NativeWindowHandle = 0);

public interface IEventEnrichmentPipeline
{
    Task<ActivityEvent> EnrichAsync(EventProcessingRequest request, CancellationToken ct);
}
