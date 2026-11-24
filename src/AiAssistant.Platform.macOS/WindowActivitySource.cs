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
/// macOS NSWorkspace + AXUIElement foreground window adapter over Swift Helper IPC.
/// Resilient across helper restarts: transient IPC disconnects skip ticks without terminating the capture stream.
/// </summary>
public sealed class WindowActivitySource(
    IMacHelperSupervisor supervisor,
    IClock clock,
    IScreenCapture? screenCapture = null,
    TimeSpan? pollInterval = null) : IWindowActivitySource
{
    private readonly TimeSpan _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(400);

    public SourceKind Kind => SourceKind.WindowActivity;

    public CapabilityState Capability => MacHelperCapabilityEvaluator.EvaluateHelperCapability(
        supervisor,
        CapabilityKind.WindowActivity,
        supervisor.Permissions.Accessibility,
        "NSWorkspace + AXUIElement 前台窗口识别",
        "请在「系统设置 > 隐私与安全性 > 辅助功能」中启用 AiAssistant 以获取完整窗口标题。",
        degradeInsteadOfRequirePermission: true);

    public async Task<CapturedSample?> GetCurrentForegroundAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var response = await supervisor.InvokeAsync(
            new MacIpcRequest(string.Empty, MacHelperProtocol.Methods.ForegroundWindow),
            ct);

        if (!response.Ok || response.Window is null || string.IsNullOrWhiteSpace(response.Window.AppName))
        {
            return null;
        }

        var meta = string.IsNullOrWhiteSpace(response.Window.BundleId)
            ? null
            : new Dictionary<string, string>(StringComparer.Ordinal) { ["bundleId"] = response.Window.BundleId! };

        var title = string.IsNullOrWhiteSpace(response.Window.WindowTitle)
            ? response.Window.AppName
            : response.Window.WindowTitle;

        return new CapturedSample(
            clock.Now(),
            SourceKind.WindowActivity,
            response.Window.AppName,
            title,
            NativeWindowHandle: (nint)response.Window.WindowId,
            Metadata: meta);
    }

    public async IAsyncEnumerable<CapturedSample> CaptureAsync(
        CaptureContext context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var fallbackInterval = context.FallbackInterval <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(10)
            : context.FallbackInterval;

        string? lastSignature = null;
        var lastEmitUtc = DateTimeOffset.MinValue;

        using var timer = new PeriodicTimer(_pollInterval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            if (supervisor.Status is MacHelperSupervisorStatus.Unavailable or MacHelperSupervisorStatus.ProtocolIncompatible)
            {
                yield break;
            }

            var current = await GetCurrentForegroundAsync(ct);
            if (current is null)
            {
                continue;
            }

            var signature = $"{current.AppName}\u001F{current.WindowTitle}";
            var nowUtc = clock.UtcNow;
            var isChanged = !string.Equals(signature, lastSignature, StringComparison.Ordinal);
            var isFallbackDue = (nowUtc - lastEmitUtc) >= fallbackInterval;

            if (!isChanged && !isFallbackDue)
            {
                continue;
            }

            lastSignature = signature;
            lastEmitUtc = nowUtc;

            if (screenCapture is not null && screenCapture.Capability.IsOperational)
            {
                var frames = await screenCapture.CaptureDisplaysAsync(
                    new ScreenCaptureTarget(current.NativeWindowHandle),
                    ct);
                if (frames.Count > 0)
                {
                    foreach (var frame in frames)
                    {
                        yield return current with
                        {
                            Timestamp = frame.Timestamp,
                            Source = SourceKind.ScreenFrame,
                            RawMediaBytes = frame.EncodedPngBytes,
                            MediaKind = ContentKind.ScreenshotPng,
                            PerceptualHash = frame.PerceptualHash,
                            Metadata = ScreenCaptureMetadata.FromDisplay(frame)
                        };
                    }

                    continue;
                }
            }

            yield return current;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
