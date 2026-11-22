using AiAssistant.Application.Imaging;
using System.Runtime.CompilerServices;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Platform.Linux.Accessibility;
using AiAssistant.Platform.Linux.Desktop;
using AiAssistant.Platform.Linux.PipeWire;
using AiAssistant.Platform.Linux.Portal;
using AiAssistant.Platform.Linux.X11;
using Microsoft.Extensions.Logging;

namespace AiAssistant.Platform.Linux;

public sealed class WaylandPipeWireOptions
{
    /// <summary>Continuous PipeWire streaming is the default on Wayland; screenshots stay the fallback.</summary>
    public bool EnablePipeWire { get; init; } = true;

    /// <summary>How long a capture waits for the next PipeWire frame before falling back.</summary>
    public TimeSpan FrameTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Reuse a cached frame when it is at most this old.</summary>
    public TimeSpan MaxFrameAge { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Minimum spacing between copied frames; protects CPU and memory at monitor refresh rates.</summary>
    public TimeSpan MinFrameInterval { get; init; } = TimeSpan.FromSeconds(1);
}

public sealed class WaylandScreenCapture(
    IXdgPortalClient portalClient,
    XdgPortalSessionManager sessionManager,
    IClock clock,
    ILogger<WaylandScreenCapture> logger,
    PipeWireScreenCastStream? pipeWireStream = null,
    WaylandPipeWireOptions? pipeWireOptions = null) : IScreenCapture
{
    private readonly SemaphoreSlim _streamGate = new(1, 1);
    private bool _streamStarted;

    public CapabilityState Capability
    {
        get
        {
            if (pipeWireStream is not null &&
                (pipeWireOptions?.EnablePipeWire ?? true) &&
                pipeWireStream.Capability.IsOperational)
            {
                return pipeWireStream.Capability;
            }

            return sessionManager.Snapshot.Status switch
            {
                XdgPortalSessionStatus.Unavailable => CapabilityState.Unavailable(CapabilityKind.ScreenCapture, sessionManager.Snapshot.Summary),
                XdgPortalSessionStatus.Degraded => CapabilityState.Degraded(CapabilityKind.ScreenCapture, sessionManager.Snapshot.Summary, "请检查 xdg-desktop-portal 及其桌面后端。"),
                _ => CapabilityState.Degraded(
                    CapabilityKind.ScreenCapture,
                    $"Wayland portal on-demand capture; {sessionManager.Snapshot.Summary}",
                    "Authorize the xdg-desktop-portal screen dialog. Screenshots remain available when the PipeWire stream cannot start.")
            };
        }
    }

    public async Task<ScreenCaptureFrame?> CaptureFrameAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var session = await sessionManager.EnsureSessionAsync(ct);
            if (session is null)
            {
                return null;
            }

            if (pipeWireStream is not null && (pipeWireOptions?.EnablePipeWire ?? true))
            {
                await EnsurePipeWireStartedAsync(session, ct);
                var frame = await pipeWireStream.CaptureAsync(
                    pipeWireOptions?.MaxFrameAge ?? TimeSpan.FromSeconds(2),
                    pipeWireOptions?.FrameTimeout ?? TimeSpan.FromSeconds(5),
                    ct);
                if (frame is not null)
                {
                    var frameHash = PerceptualHash.ComputeFromBgra32(
                        frame.Bgra32,
                        frame.Width,
                        frame.Height,
                        frame.Width * 4);
                    var framePng = PngCodec.EncodeBgra32(frame.Bgra32, frame.Width, frame.Height);
                    return new ScreenCaptureFrame(clock.Now(), frame.Width, frame.Height, frameHash, framePng);
                }
            }

            // Fallback: portal Screenshot returns a bounded PNG when the continuous stream is unavailable.
            var png = await portalClient.CaptureScreenshotAsync(ct);
            if (!PngCodec.TryDecodeToBgra32(png, out var bgra, out var width, out var height, out var hash) || bgra is null)
            {
                return null;
            }

            return new ScreenCaptureFrame(clock.Now(), width, height, hash, png);
        }
        catch (XdgPortalRequestException)
        {
            await ResetStreamAsync();
            await sessionManager.InvalidateAsync(clearRestoreToken: true, ct);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Wayland portal capture skipped after {ExceptionType}.", ex.GetType().Name);
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        pipeWireStream?.Stop();
        _streamGate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task EnsurePipeWireStartedAsync(PortalScreenCastSession session, CancellationToken ct)
    {
        if (_streamStarted)
        {
            return;
        }

        await _streamGate.WaitAsync(ct);
        try
        {
            if (_streamStarted)
            {
                return;
            }

            var descriptor = session.Streams[0];
            using var remote = await portalClient.OpenPipeWireRemoteAsync(session.SessionPath, ct);
            var serial = descriptor.PipeWireSerial is > 0 ? (uint)descriptor.PipeWireSerial.Value : (uint?)null;
            if (pipeWireStream!.TryStart(
                    remote,
                    serial,
                    descriptor.NodeId,
                    Math.Max(descriptor.Width, 1),
                    Math.Max(descriptor.Height, 1),
                    out var error))
            {
                _streamStarted = true;
            }
            else
            {
                logger.LogInformation("PipeWire stream unavailable; using portal screenshots ({Reason}).", error);
            }
        }
        finally
        {
            _streamGate.Release();
        }
    }

    private async Task ResetStreamAsync()
    {
        pipeWireStream?.Stop();
        await _streamGate.WaitAsync();
        try
        {
            _streamStarted = false;
        }
        finally
        {
            _streamGate.Release();
        }
    }
}

/// <summary>Dynamic screen adapter that follows Wayland to X11 runtime fallback without restarting the capture pipeline.</summary>
public sealed class LinuxScreenCapture : IScreenCapture
{
    private readonly LinuxDisplayBackendState _backend;
    private readonly WaylandScreenCapture _wayland;
    private readonly X11ScreenCapture _x11;

    public LinuxScreenCapture(
        LinuxDisplayBackendState backend,
        WaylandScreenCapture wayland,
        X11ScreenCapture x11)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(wayland);
        ArgumentNullException.ThrowIfNull(x11);
        _backend = backend;
        _wayland = wayland;
        _x11 = x11;
    }

    public CapabilityState Capability => !OperatingSystem.IsLinux()
        ? CapabilityState.Unavailable(CapabilityKind.ScreenCapture, "Linux 采集在当前主机不可用")
        : _backend.Decision.Backend == LinuxDisplayBackendKind.Wayland ? _wayland.Capability : _x11.Capability;

    public Task<ScreenCaptureFrame?> CaptureFrameAsync(CancellationToken ct) =>
        _backend.Decision.Backend == LinuxDisplayBackendKind.Wayland
            ? _wayland.CaptureFrameAsync(ct)
            : _x11.CaptureFrameAsync(ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class LinuxWindowActivitySource(
    LinuxDisplayBackendState backend,
    IAtSpiAccessibilityClient atSpi,
    IClock clock,
    X11ScreenCapture x11ScreenCapture) : IWindowActivitySource
{
    private readonly AtSpiWindowActivitySource _waylandSource = new(atSpi, clock);
    private readonly X11WindowActivitySource _x11Source = new(clock);

    public SourceKind Kind => SourceKind.WindowActivity;

    public CapabilityState Capability => !OperatingSystem.IsLinux()
        ? CapabilityState.Unavailable(CapabilityKind.WindowActivity, "Linux 窗口采集在当前主机不可用")
        : backend.Decision.Backend == LinuxDisplayBackendKind.Wayland ? _waylandSource.Capability : _x11Source.Capability;

    public Task<CapturedSample?> GetCurrentForegroundAsync(CancellationToken ct) =>
        backend.Decision.Backend == LinuxDisplayBackendKind.Wayland
            ? _waylandSource.GetCurrentForegroundAsync(ct)
            : _x11Source.GetCurrentForegroundAsync(ct);

    public async IAsyncEnumerable<CapturedSample> CaptureAsync(CaptureContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        string? lastSignature = null;
        var lastEmit = DateTimeOffset.MinValue;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(450));
        while (await timer.WaitForNextTickAsync(ct))
        {
            var isWayland = backend.Decision.Backend == LinuxDisplayBackendKind.Wayland;
            var current = isWayland
                ? await _waylandSource.GetCurrentForegroundAsync(ct)
                : await _x11Source.GetCurrentForegroundAsync(ct);
            if (current is null)
            {
                continue;
            }

            var signature = $"{current.AppName}\u001F{current.WindowTitle}";
            if (string.Equals(signature, lastSignature, StringComparison.Ordinal) && clock.UtcNow - lastEmit < context.FallbackInterval)
            {
                continue;
            }

            lastSignature = signature;
            lastEmit = clock.UtcNow;

            // Only X11 attaches continuous screen frames; Wayland portal capture is user-triggered to avoid recurring prompts.
            if (!isWayland && x11ScreenCapture.Capability.IsOperational)
            {
                var frame = await x11ScreenCapture.CaptureFrameAsync(ct);
                if (frame is not null)
                {
                    yield return current with
                    {
                        Source = SourceKind.ScreenFrame,
                        RawMediaBytes = frame.EncodedPngBytes,
                        MediaKind = ContentKind.ScreenshotPng,
                        PerceptualHash = frame.PerceptualHash
                    };
                    continue;
                }
            }

            yield return current;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class LinuxTextExtractor(
    IAtSpiAccessibilityClient atSpi,
    IImageOcrRecognizer? ocr = null) : ITextExtractor
{
    public CapabilityState AccessibilityCapability => atSpi.Capability;

    public CapabilityState OcrCapability => ocr is not null
        ? ocr.Capability
        : CapabilityState.Degraded(
            CapabilityKind.Ocr,
            "RapidOCR models are not configured; AT-SPI accessibility remains available",
            "请设置 AIASSISTANT_RAPIDOCR_MODEL_DIR 以启用 ONNX 图像 OCR 兜底。");

    public async Task<ExtractedTextResult> ExtractAsync(TextExtractionRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var text = await atSpi.ExtractTextAsync(request.WindowHandle, ct);
        if (!string.IsNullOrWhiteSpace(text))
        {
            return new ExtractedTextResult(text, TextExtractionMethod.AccessibilityTree, 0.90);
        }

        if (ocr is not null &&
            request.ScreenshotPngBytes is { Length: > 0 } png &&
            PngCodec.TryDecodeToBgra32(png, out var bgra, out var width, out var height, out _) &&
            bgra is not null)
        {
            var ocrText = await ocr.RecognizeAsync(new ImageFrameBuffer(bgra, width, height), ct);
            if (!string.IsNullOrWhiteSpace(ocrText))
            {
                return new ExtractedTextResult(ocrText.Trim(), TextExtractionMethod.SystemOcr, 0.75);
            }
        }

        var fallback = string.IsNullOrWhiteSpace(request.WindowTitle)
            ? request.AppName
            : $"{request.AppName} - {request.WindowTitle}";
        return new ExtractedTextResult(fallback, TextExtractionMethod.None, 0.50);
    }
}
