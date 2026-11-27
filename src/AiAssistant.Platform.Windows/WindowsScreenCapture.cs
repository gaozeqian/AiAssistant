using System.Globalization;
using System.Runtime.InteropServices;
using AiAssistant.Application.Ports;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Platform.Windows;

/// <summary>
/// Captures every connected display via Win32 GDI StretchBlt (downscaled to &lt;= <see cref="ScreenCaptureLimits.MaxFrameWidth"/>px
/// width with HALFTONE averaging so downscaled text stays legible), computes a 64-bit perceptual hash per
/// display, and encodes PNG in memory. The display hosting the foreground window is flagged so the window
/// activity record keeps its context while the remaining monitors still get their own record.
/// </summary>
public sealed class WindowsScreenCapture(IClock clock) : IScreenCapture
{
    public CapabilityState Capability => OperatingSystem.IsWindows()
        ? CapabilityState.Supported(CapabilityKind.ScreenCapture, "Win32 GDI 逐显示器截图（PNG + 感知哈希）")
        : CapabilityState.Unavailable(CapabilityKind.ScreenCapture, "Windows 屏幕采集仅在 Windows 主机可用");

    public Task<IReadOnlyList<ScreenCaptureFrame>> CaptureDisplaysAsync(ScreenCaptureTarget target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult<IReadOnlyList<ScreenCaptureFrame>>([]);
        }

        var monitors = EnumerateMonitors(target.WindowHandle);
        if (monitors.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<ScreenCaptureFrame>>([]);
        }

        var frames = new List<ScreenCaptureFrame>(monitors.Count);
        foreach (var monitor in monitors)
        {
            var frame = CaptureMonitor(monitor);
            if (frame is not null)
            {
                frames.Add(frame);
            }
        }

        return Task.FromResult<IReadOnlyList<ScreenCaptureFrame>>(frames);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed record MonitorTarget(
        nint Handle,
        string DeviceName,
        Win32Native.NativeRect Bounds,
        bool IsPrimary,
        bool HasForegroundWindow);

    private static List<MonitorTarget> EnumerateMonitors(nint foregroundWindow)
    {
        var foregroundMonitor = foregroundWindow != 0
            ? Win32Native.MonitorFromWindow(foregroundWindow, Win32Native.MonitorDefaultToNearest)
            : Win32Native.MonitorFromPoint(new Win32Native.NativePoint(), Win32Native.MonitorDefaultToPrimary);

        var monitors = new List<MonitorTarget>();
        // The delegate must stay alive for the duration of the enumeration.
        Win32Native.MonitorEnumProc callback = (hMonitor, _, ref bounds, _) =>
        {
            var info = new Win32Native.MonitorInfoEx
            {
                Size = (uint)Marshal.SizeOf<Win32Native.MonitorInfoEx>()
            };
            if (!Win32Native.GetMonitorInfo(hMonitor, ref info))
            {
                return true;
            }

            monitors.Add(new MonitorTarget(
                hMonitor,
                string.IsNullOrWhiteSpace(info.DeviceName)
                    ? string.Create(CultureInfo.InvariantCulture, $"DISPLAY{monitors.Count + 1}")
                    : info.DeviceName,
                info.Monitor.Width > 0 && info.Monitor.Height > 0 ? info.Monitor : bounds,
                (info.Flags & Win32Native.MonitorInfoPrimary) != 0,
                hMonitor == foregroundMonitor));
            return true;
        };

        if (!Win32Native.EnumDisplayMonitors(0, 0, callback, 0) || monitors.Count == 0)
        {
            monitors.Clear();
            monitors.Add(CreatePrimaryFallback(foregroundMonitor));
        }

        return monitors;
    }

    /// <summary>Fallback for environments where monitor enumeration fails: primary screen bounds.</summary>
    private static MonitorTarget CreatePrimaryFallback(nint foregroundMonitor)
    {
        var width = Win32Native.GetSystemMetrics(Win32Native.SmCxScreen);
        var height = Win32Native.GetSystemMetrics(Win32Native.SmCyScreen);
        var handle = Win32Native.MonitorFromPoint(new Win32Native.NativePoint(), Win32Native.MonitorDefaultToPrimary);
        return new MonitorTarget(
            handle,
            "DISPLAY1",
            new Win32Native.NativeRect { Left = 0, Top = 0, Right = width, Bottom = height },
            IsPrimary: true,
            HasForegroundWindow: handle == foregroundMonitor || foregroundMonitor == 0);
    }

    /// <summary>"\\.\DISPLAY2" becomes "2" so the timeline reads 「显示器 2」 instead of the raw device path.</summary>
    private static string FriendlyDisplayName(string deviceName)
    {
        var digits = new string(deviceName.Where(char.IsDigit).ToArray());
        return string.IsNullOrEmpty(digits) ? deviceName : digits;
    }

    private ScreenCaptureFrame? CaptureMonitor(MonitorTarget monitor)
    {
        var srcWidth = monitor.Bounds.Width;
        var srcHeight = monitor.Bounds.Height;
        if (srcWidth <= 0 || srcHeight <= 0)
        {
            return null;
        }

        var targetWidth = Math.Min(srcWidth, ScreenCaptureLimits.MaxFrameWidth);
        var targetHeight = Math.Max(1, (int)Math.Round((double)srcHeight * targetWidth / srcWidth));

        var screenDc = Win32Native.GetDC(0);
        if (screenDc == 0)
        {
            return null;
        }

        var memDc = Win32Native.CreateCompatibleDC(screenDc);
        nint dib = 0;
        nint oldObj = 0;

        try
        {
            var bmi = new Win32Native.BitmapInfo
            {
                BmiHeader = new Win32Native.BitmapInfoHeader
                {
                    BiSize = (uint)Marshal.SizeOf<Win32Native.BitmapInfoHeader>(),
                    BiWidth = targetWidth,
                    BiHeight = -targetHeight, // Top-down DIB
                    BiPlanes = 1,
                    BiBitCount = 32,
                    BiCompression = 0
                }
            };

            dib = Win32Native.CreateDIBSection(memDc, ref bmi, 0, out var bitsPtr, 0, 0);
            if (dib == 0 || bitsPtr == 0)
            {
                return null;
            }

            oldObj = Win32Native.SelectObject(memDc, dib);
            // HALFTONE averages source pixels instead of dropping them (COLORONCOLOR), which is the
            // difference between unreadable and readable text once a 2560px display is shrunk. GDI
            // requires the brush origin to be re-aligned after selecting HALFTONE; without it the
            // halftone pattern is offset per blit and the frame picks up a colored cast.
            if (Win32Native.SetStretchBltMode(memDc, Win32Native.Halftone) == 0)
            {
                // Driver refused HALFTONE (rare for memory DCs); COLORONCOLOR still produces a frame.
                _ = Win32Native.SetStretchBltMode(memDc, Win32Native.ColorOnColor);
            }
            else
            {
                _ = Win32Native.SetBrushOrgEx(memDc, 0, 0, 0);
            }
            // The desktop DC spans the virtual screen, so a monitor left/above the primary has
            // negative source coordinates; StretchBlt accepts them relative to the primary origin.
            var ok = Win32Native.StretchBlt(
                memDc, 0, 0, targetWidth, targetHeight,
                screenDc, monitor.Bounds.Left, monitor.Bounds.Top, srcWidth, srcHeight,
                Win32Native.Srccopy);

            if (!ok)
            {
                return null;
            }

            var stride = targetWidth * 4;
            var rawPixels = new byte[stride * targetHeight];
            Marshal.Copy(bitsPtr, rawPixels, 0, rawPixels.Length);

            var phash = PerceptualHash.ComputeFromBgra32(rawPixels, targetWidth, targetHeight, stride);
            var pngBytes = PngEncoder.EncodeBgra32(rawPixels, targetWidth, targetHeight, stride);
            var display = new DisplayInfo(
                monitor.DeviceName,
                FriendlyDisplayName(monitor.DeviceName),
                monitor.Bounds.Left,
                monitor.Bounds.Top,
                srcWidth,
                srcHeight,
                monitor.IsPrimary);

            return new ScreenCaptureFrame(
                clock.Now(),
                targetWidth,
                targetHeight,
                phash,
                pngBytes,
                display,
                monitor.HasForegroundWindow);
        }
        finally
        {
            if (oldObj != 0 && memDc != 0)
            {
                _ = Win32Native.SelectObject(memDc, oldObj);
            }

            if (dib != 0)
            {
                _ = Win32Native.DeleteObject(dib);
            }

            if (memDc != 0)
            {
                _ = Win32Native.DeleteDC(memDc);
            }

            _ = Win32Native.ReleaseDC(0, screenDc);
        }
    }
}
