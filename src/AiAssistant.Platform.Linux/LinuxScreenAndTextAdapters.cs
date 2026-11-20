using System.Runtime.CompilerServices;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Platform.Linux.Accessibility;
using AiAssistant.Platform.Linux.Desktop;
using AiAssistant.Platform.Linux.Portal;
using AiAssistant.Platform.Linux.X11;
using Microsoft.Extensions.Logging;

namespace AiAssistant.Platform.Linux;

public sealed class WaylandScreenCapture(
    IXdgPortalClient portalClient,
    XdgPortalSessionManager sessionManager,
    IClock clock,
    ILogger<WaylandScreenCapture> logger) : IScreenCapture
{
    public CapabilityState Capability => sessionManager.Snapshot.Status switch
    {
        XdgPortalSessionStatus.Unavailable => CapabilityState.Unavailable(CapabilityKind.ScreenCapture, sessionManager.Snapshot.Summary),
        XdgPortalSessionStatus.Degraded => CapabilityState.Degraded(CapabilityKind.ScreenCapture, sessionManager.Snapshot.Summary, "Check xdg-desktop-portal and its desktop backend."),
        _ => CapabilityState.Degraded(
            CapabilityKind.ScreenCapture,
            $"Wayland portal on-demand capture; {sessionManager.Snapshot.Summary}",
            "Authorize the xdg-desktop-portal screen dialog. Continuous PipeWire frame consumption is validated on a Linux desktop.")
    };

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

            // Safe fallback: portal Screenshot returns a bounded PNG while native PipeWire pixel streaming remains a real-desktop validation item.
            var png = await portalClient.CaptureScreenshotAsync(ct);
            if (!LinuxPngCodec.TryDecodeToBgra32(png, out var bgra, out var width, out var height, out var hash) || bgra is null)
            {
                return null;
            }

            return new ScreenCaptureFrame(clock.Now(), width, height, hash, png);
        }
        catch (XdgPortalRequestException)
        {
            await sessionManager.InvalidateAsync(clearRestoreToken: true, ct);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Wayland portal capture skipped after {ExceptionType}.", ex.GetType().Name);
            return null;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
        ? CapabilityState.Unavailable(CapabilityKind.ScreenCapture, "Linux capture is inactive on this host")
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
        ? CapabilityState.Unavailable(CapabilityKind.WindowActivity, "Linux window capture is inactive on this host")
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

public sealed class LinuxTextExtractor(IAtSpiAccessibilityClient atSpi) : ITextExtractor
{
    public CapabilityState AccessibilityCapability => atSpi.Capability;

    public CapabilityState OcrCapability => CapabilityState.Degraded(
        CapabilityKind.Ocr,
        "RapidOCR ONNX fallback is scheduled for S4; AT-SPI accessibility remains available");

    public async Task<ExtractedTextResult> ExtractAsync(TextExtractionRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var text = await atSpi.ExtractTextAsync(request.WindowHandle, ct);
        if (!string.IsNullOrWhiteSpace(text))
        {
            return new ExtractedTextResult(text, TextExtractionMethod.AccessibilityTree, 0.90);
        }

        var fallback = string.IsNullOrWhiteSpace(request.WindowTitle)
            ? request.AppName
            : $"{request.AppName} - {request.WindowTitle}";
        return new ExtractedTextResult(fallback, TextExtractionMethod.None, 0.50);
    }
}
