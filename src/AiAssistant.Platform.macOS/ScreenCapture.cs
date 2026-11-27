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
/// macOS ScreenCaptureKit adapter over versioned Swift Helper IPC (ADR-0005, S3). The helper
/// captures every connected display and flags the one hosting the frontmost window.
/// </summary>
public sealed class ScreenCapture(IMacHelperSupervisor supervisor, IClock clock) : IScreenCapture
{
    public CapabilityState Capability => MacHelperCapabilityEvaluator.EvaluateHelperCapability(
        supervisor,
        CapabilityKind.ScreenCapture,
        supervisor.Permissions.ScreenRecording,
        "ScreenCaptureKit（Swift Helper，逐显示器 ≤1920px PNG + 感知哈希）",
        "请在「系统设置 > 隐私与安全性 > 屏幕与系统音频录制」中启用 AiAssistant。");

    public async Task<IReadOnlyList<ScreenCaptureFrame>> CaptureDisplaysAsync(ScreenCaptureTarget target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var response = await supervisor.InvokeAsync(
            new MacIpcRequest(string.Empty, MacHelperProtocol.Methods.ScreenCapture, MaxWidth: ScreenCaptureLimits.MaxFrameWidth),
            ct);

        if (!response.Ok)
        {
            return [];
        }

        var dtos = response.ScreenFrames is { Count: > 0 }
            ? response.ScreenFrames
            : response.ScreenFrame is null ? [] : [response.ScreenFrame];

        var frames = new List<ScreenCaptureFrame>(dtos.Count);
        foreach (var dto in dtos)
        {
            if (string.IsNullOrWhiteSpace(dto.PngBase64))
            {
                continue;
            }

            byte[] pngBytes;
            try
            {
                pngBytes = Convert.FromBase64String(dto.PngBase64);
            }
            catch (FormatException)
            {
                continue;
            }

            if (pngBytes.Length == 0)
            {
                continue;
            }

            DisplayInfo? display = null;
            if (!string.IsNullOrWhiteSpace(dto.DisplayId) &&
                dto.DisplayLeft is { } left &&
                dto.DisplayTop is { } top &&
                dto.DisplayWidth is { } width &&
                dto.DisplayHeight is { } height)
            {
                display = new DisplayInfo(
                    dto.DisplayId,
                    string.IsNullOrWhiteSpace(dto.DisplayName) ? dto.DisplayId : dto.DisplayName,
                    left,
                    top,
                    width,
                    height,
                    dto.IsPrimary ?? false);
            }

            frames.Add(new ScreenCaptureFrame(
                clock.Now(),
                dto.Width,
                dto.Height,
                dto.PerceptualHash,
                pngBytes,
                display,
                dto.IsForegroundDisplay ?? (display is null)));
        }

        return frames;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
