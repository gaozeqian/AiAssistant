using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public sealed record CaptureContext(
    IReadOnlyList<string> WhitelistedDirectories,
    TimeSpan FallbackInterval,
    int MaxTextFileBytes = 200 * 1024);

public sealed record CapturedSample(
    OccurredTimestamp Timestamp,
    SourceKind Source,
    string AppName,
    string WindowTitle,
    string? ExtractedText = null,
    byte[]? RawMediaBytes = null,
    ContentKind? MediaKind = null,
    ulong? PerceptualHash = null,
    nint NativeWindowHandle = 0,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>
/// Streaming capture source contract (IMPLEMENTATION.md §3.3).
/// </summary>
public interface ICaptureSource : IAsyncDisposable
{
    SourceKind Kind { get; }
    CapabilityState Capability { get; }
    IAsyncEnumerable<CapturedSample> CaptureAsync(CaptureContext context, CancellationToken ct);
}

public interface IWindowActivitySource : ICaptureSource
{
    Task<CapturedSample?> GetCurrentForegroundAsync(CancellationToken ct);
}

public interface IScreenCapture : IAsyncDisposable
{
    CapabilityState Capability { get; }
    Task<ScreenCaptureFrame?> CaptureFrameAsync(CancellationToken ct);
}

public sealed record ScreenCaptureFrame(
    OccurredTimestamp Timestamp,
    int Width,
    int Height,
    ulong PerceptualHash,
    byte[] EncodedPngBytes);

public interface IFileActivitySource : ICaptureSource;

public interface IAudioCapture : IAsyncDisposable
{
    CapabilityState Capability { get; }
    IAsyncEnumerable<AudioFrame> ReadFramesAsync(CancellationToken ct);
}

/// <summary>Creates a fresh single-consumer microphone capture when the AudioHub has subscribers.</summary>
public interface IAudioCaptureFactory
{
    CapabilityState Capability { get; }
    IAudioCapture Create();
}

public sealed record AudioFrame(
    DateTimeOffset TimestampUtc,
    int SampleRateHz,
    int Channels,
    ReadOnlyMemory<float> Samples);

/// <summary>
/// Single-microphone hub multiplexing audio frames to KWS, VAD, and ASR without device contention (ADR-0007).
/// </summary>
public sealed record AudioSubscription(string SubscriberName, bool IncludePreRollRingBuffer = false);

public interface IAudioHub : IAsyncDisposable
{
    CapabilityState Capability { get; }
    int ActiveSubscriberCount { get; }
    IAsyncEnumerable<AudioFrame> SubscribeAsync(AudioSubscription subscription, CancellationToken ct);
}
