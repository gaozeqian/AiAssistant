using System.Diagnostics;
using System.Runtime.CompilerServices;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Platform.Windows;

/// <summary>
/// Event-driven foreground window &amp; focus source with 10s periodic fallback heartbeat (design.md §3.2, §9).
/// Never captures keystrokes or clipboard contents.
/// </summary>
public sealed class WindowsWindowActivitySource(IClock clock, IScreenCapture? screenCapture = null) : IWindowActivitySource
{
    public SourceKind Kind => SourceKind.WindowActivity;

    public CapabilityState Capability => OperatingSystem.IsWindows()
        ? CapabilityState.Supported(CapabilityKind.WindowActivity, "Win32 前台窗口 + 进程信息")
        : CapabilityState.Unavailable(CapabilityKind.WindowActivity, "Windows 适配器仅在 Windows 主机可用");

    public Task<CapturedSample?> GetCurrentForegroundAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult<CapturedSample?>(null);
        }

        var hwnd = Win32Native.GetForegroundWindow();
        if (hwnd == 0)
        {
            return Task.FromResult<CapturedSample?>(null);
        }

        var titleLen = Win32Native.GetWindowTextLengthW(hwnd);
        var titleBuffer = new char[Math.Max(titleLen + 2, 256)];
        var copied = Win32Native.GetWindowTextW(hwnd, titleBuffer, titleBuffer.Length);
        var title = copied > 0 ? new string(titleBuffer, 0, copied).Trim() : string.Empty;

        _ = Win32Native.GetWindowThreadProcessId(hwnd, out var pid);
        var appName = TryResolveProcessName((int)pid);
        if (string.IsNullOrWhiteSpace(title) && string.Equals(appName, "Unknown", StringComparison.Ordinal))
        {
            return Task.FromResult<CapturedSample?>(null);
        }

        var sample = new CapturedSample(
            clock.Now(),
            SourceKind.WindowActivity,
            appName,
            string.IsNullOrWhiteSpace(title) ? appName : title,
            NativeWindowHandle: hwnd);

        return Task.FromResult<CapturedSample?>(sample);
    }

    public async IAsyncEnumerable<CapturedSample> CaptureAsync(
        CaptureContext context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        var fallbackInterval = context.FallbackInterval <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(10)
            : context.FallbackInterval;

        string? lastSignature = null;
        var lastEmitUtc = DateTimeOffset.MinValue;

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(400));
        while (await timer.WaitForNextTickAsync(ct))
        {
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
                var frame = await screenCapture.CaptureFrameAsync(ct);
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

    private static string TryResolveProcessName(int pid)
    {
        if (pid <= 0)
        {
            return "Unknown";
        }

        try
        {
            using var proc = Process.GetProcessById(pid);
            return string.IsNullOrWhiteSpace(proc.ProcessName) ? "Unknown" : proc.ProcessName;
        }
        catch
        {
            return "Unknown";
        }
    }
}
