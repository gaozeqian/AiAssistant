using System.Runtime.InteropServices;
using AiAssistant.Application.Ports;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Platform.Windows;

/// <summary>
/// Captures desktop screen via Win32 GDI StretchBlt (downscaled to <= 1280px width for <1% CPU overhead),
/// computes 64-bit perceptual hash, and encodes PNG in memory.
/// </summary>
public sealed class WindowsScreenCapture(IClock clock) : IScreenCapture
{
    public CapabilityState Capability => OperatingSystem.IsWindows()
        ? CapabilityState.Supported(CapabilityKind.ScreenCapture, "Win32 GDI 截图（PNG + 感知哈希）")
        : CapabilityState.Unavailable(CapabilityKind.ScreenCapture, "Windows 屏幕采集仅在 Windows 主机可用");

    public Task<ScreenCaptureFrame?> CaptureFrameAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult<ScreenCaptureFrame?>(null);
        }

        var srcWidth = Win32Native.GetSystemMetrics(Win32Native.SmCxScreen);
        var srcHeight = Win32Native.GetSystemMetrics(Win32Native.SmCyScreen);
        if (srcWidth <= 0 || srcHeight <= 0)
        {
            return Task.FromResult<ScreenCaptureFrame?>(null);
        }

        var targetWidth = Math.Min(srcWidth, 1280);
        var targetHeight = Math.Max(1, (int)Math.Round((double)srcHeight * targetWidth / srcWidth));

        var screenDc = Win32Native.GetDC(0);
        if (screenDc == 0)
        {
            return Task.FromResult<ScreenCaptureFrame?>(null);
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
                return Task.FromResult<ScreenCaptureFrame?>(null);
            }

            oldObj = Win32Native.SelectObject(memDc, dib);
            _ = Win32Native.SetStretchBltMode(memDc, 3); // COLORONCOLOR fast downscale
            var ok = Win32Native.StretchBlt(
                memDc, 0, 0, targetWidth, targetHeight,
                screenDc, 0, 0, srcWidth, srcHeight,
                Win32Native.Srccopy);

            if (!ok)
            {
                return Task.FromResult<ScreenCaptureFrame?>(null);
            }

            var stride = targetWidth * 4;
            var rawPixels = new byte[stride * targetHeight];
            Marshal.Copy(bitsPtr, rawPixels, 0, rawPixels.Length);

            var phash = PerceptualHash.ComputeFromBgra32(rawPixels, targetWidth, targetHeight, stride);
            var pngBytes = PngEncoder.EncodeBgra32(rawPixels, targetWidth, targetHeight, stride);

            return Task.FromResult<ScreenCaptureFrame?>(
                new ScreenCaptureFrame(clock.Now(), targetWidth, targetHeight, phash, pngBytes));
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

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
