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
/// macOS ScreenCaptureKit adapter over versioned Swift Helper IPC (ADR-0005, S3).
/// </summary>
public sealed class ScreenCapture(IMacHelperSupervisor supervisor, IClock clock) : IScreenCapture
{
    public CapabilityState Capability => MacHelperCapabilityEvaluator.EvaluateHelperCapability(
        supervisor,
        CapabilityKind.ScreenCapture,
        supervisor.Permissions.ScreenRecording,
        "ScreenCaptureKit（Swift Helper，≤1280px PNG + 感知哈希）",
        "请在「系统设置 > 隐私与安全性 > 屏幕与系统音频录制」中启用 AiAssistant。");

    public async Task<ScreenCaptureFrame?> CaptureFrameAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var response = await supervisor.InvokeAsync(
            new MacIpcRequest(string.Empty, MacHelperProtocol.Methods.ScreenCapture, MaxWidth: 1280),
            ct);

        if (!response.Ok || response.ScreenFrame is null || string.IsNullOrWhiteSpace(response.ScreenFrame.PngBase64))
        {
            return null;
        }

        byte[] pngBytes;
        try
        {
            pngBytes = Convert.FromBase64String(response.ScreenFrame.PngBase64);
        }
        catch (FormatException)
        {
            return null;
        }

        if (pngBytes.Length == 0)
        {
            return null;
        }

        return new ScreenCaptureFrame(
            clock.Now(),
            response.ScreenFrame.Width,
            response.ScreenFrame.Height,
            response.ScreenFrame.PerceptualHash,
            pngBytes);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
