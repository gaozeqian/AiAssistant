using AiAssistant.Application.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Platform.Linux.X11;

internal static partial class X11Native
{
    internal const ulong AllPlanes = ulong.MaxValue;
    internal const int ZPixmap = 2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct XImageLayout
    {
        internal int Width;
        internal int Height;
        internal int XOffset;
        internal int Format;
        internal nint Data;
        internal int ByteOrder;
        internal int BitmapUnit;
        internal int BitmapBitOrder;
        internal int BitmapPad;
        internal int Depth;
        internal int BytesPerLine;
        internal int BitsPerPixel;
        internal nuint RedMask;
        internal nuint GreenMask;
        internal nuint BlueMask;
    }

    [DllImport("libX11.so.6", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    internal static extern nint XOpenDisplay(string? displayName);

    [DllImport("libX11.so.6")]
    internal static extern int XCloseDisplay(nint display);

    [DllImport("libX11.so.6")]
    internal static extern nint XDefaultRootWindow(nint display);

    [DllImport("libX11.so.6")]
    internal static extern int XDisplayWidth(nint display, int screenNumber);

    [DllImport("libX11.so.6")]
    internal static extern int XDisplayHeight(nint display, int screenNumber);

    [DllImport("libX11.so.6", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    internal static extern nuint XInternAtom(nint display, string name, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);

    [DllImport("libX11.so.6")]
    internal static extern int XGetWindowProperty(
        nint display,
        nint window,
        nuint property,
        long offset,
        long length,
        [MarshalAs(UnmanagedType.Bool)] bool delete,
        nuint requestedType,
        out nuint actualType,
        out int actualFormat,
        out nuint itemCount,
        out nuint bytesAfter,
        out nint propertyData);

    [DllImport("libX11.so.6")]
    internal static extern int XFetchName(nint display, nint window, out nint name);

    [DllImport("libX11.so.6")]
    internal static extern int XFree(nint data);

    [DllImport("libX11.so.6")]
    internal static extern nint XGetImage(
        nint display,
        nint drawable,
        int x,
        int y,
        uint width,
        uint height,
        ulong planeMask,
        int format);

    [DllImport("libX11.so.6")]
    internal static extern int XDestroyImage(nint image);
}

/// <summary>Pure XImage pixel conversion, independently testable without an X server.</summary>
public static class X11PixelConverter
{
    public static byte[] DownsampleToBgra32(
        ReadOnlySpan<byte> source,
        int sourceWidth,
        int sourceHeight,
        int stride,
        int bitsPerPixel,
        int byteOrder,
        ulong redMask,
        ulong greenMask,
        ulong blueMask,
        int targetWidth,
        int targetHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || targetWidth <= 0 || targetHeight <= 0 ||
            bitsPerPixel is not (24 or 32) || stride < sourceWidth * (bitsPerPixel / 8) ||
            source.Length < checked(stride * sourceHeight))
        {
            throw new ArgumentException("Invalid XImage buffer or dimensions.", nameof(source));
        }

        var bytesPerPixel = bitsPerPixel / 8;
        var output = new byte[checked(targetWidth * targetHeight * 4)];
        for (var y = 0; y < targetHeight; y++)
        {
            var sourceY = Math.Min(sourceHeight - 1, y * sourceHeight / targetHeight);
            for (var x = 0; x < targetWidth; x++)
            {
                var sourceX = Math.Min(sourceWidth - 1, x * sourceWidth / targetWidth);
                var index = sourceY * stride + sourceX * bytesPerPixel;
                uint pixel;
                if (byteOrder == 0)
                {
                    pixel = source[index];
                    pixel |= (uint)source[index + 1] << 8;
                    pixel |= (uint)source[index + 2] << 16;
                    if (bytesPerPixel == 4)
                    {
                        pixel |= (uint)source[index + 3] << 24;
                    }
                }
                else
                {
                    pixel = source[index + bytesPerPixel - 1];
                    pixel |= (uint)source[index + bytesPerPixel - 2] << 8;
                    pixel |= (uint)source[index + bytesPerPixel - 3] << 16;
                    if (bytesPerPixel == 4)
                    {
                        pixel |= (uint)source[index] << 24;
                    }
                }

                var target = (y * targetWidth + x) * 4;
                output[target] = ScaleMask(pixel, blueMask);
                output[target + 1] = ScaleMask(pixel, greenMask);
                output[target + 2] = ScaleMask(pixel, redMask);
                output[target + 3] = 255;
            }
        }

        return output;
    }

    private static byte ScaleMask(uint pixel, ulong mask)
    {
        if (mask == 0)
        {
            return 0;
        }

        var shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        var max = mask >> shift;
        var value = (pixel & mask) >> shift;
        return (byte)Math.Min(255UL, value * 255UL / max);
    }
}

public sealed class X11ScreenCapture(IClock clock) : IScreenCapture
{
    public CapabilityState Capability => OperatingSystem.IsLinux() && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"))
        ? CapabilityState.Supported(CapabilityKind.ScreenCapture, "X11 XGetImage root capture with PNG + pHash")
        : CapabilityState.Unavailable(CapabilityKind.ScreenCapture, "X11 display is unavailable");

    public Task<ScreenCaptureFrame?> CaptureFrameAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Capability.IsOperational)
        {
            return Task.FromResult<ScreenCaptureFrame?>(null);
        }

        nint display = 0;
        nint image = 0;
        try
        {
            display = X11Native.XOpenDisplay(null);
            if (display == 0)
            {
                return Task.FromResult<ScreenCaptureFrame?>(null);
            }

            var root = X11Native.XDefaultRootWindow(display);
            var width = X11Native.XDisplayWidth(display, 0);
            var height = X11Native.XDisplayHeight(display, 0);
            if (root == 0 || width <= 0 || height <= 0 || width > 16384 || height > 16384)
            {
                return Task.FromResult<ScreenCaptureFrame?>(null);
            }

            image = X11Native.XGetImage(display, root, 0, 0, (uint)width, (uint)height, X11Native.AllPlanes, X11Native.ZPixmap);
            if (image == 0)
            {
                return Task.FromResult<ScreenCaptureFrame?>(null);
            }

            var layout = Marshal.PtrToStructure<X11Native.XImageLayout>(image);
            if (layout.Data == 0 || layout.BytesPerLine <= 0 || layout.BitsPerPixel is not (24 or 32))
            {
                return Task.FromResult<ScreenCaptureFrame?>(null);
            }

            var byteCount = checked(layout.BytesPerLine * layout.Height);
            if (byteCount <= 0 || byteCount > 512 * 1024 * 1024)
            {
                return Task.FromResult<ScreenCaptureFrame?>(null);
            }

            var raw = new byte[byteCount];
            Marshal.Copy(layout.Data, raw, 0, byteCount);
            var targetWidth = Math.Min(width, 1280);
            var targetHeight = Math.Max(1, (int)Math.Round((double)height * targetWidth / width));
            var bgra = X11PixelConverter.DownsampleToBgra32(
                raw,
                width,
                height,
                layout.BytesPerLine,
                layout.BitsPerPixel,
                layout.ByteOrder,
                layout.RedMask,
                layout.GreenMask,
                layout.BlueMask,
                targetWidth,
                targetHeight);
            var hash = PerceptualHash.ComputeFromBgra32(bgra, targetWidth, targetHeight, targetWidth * 4);
            var png = PngCodec.EncodeBgra32(bgra, targetWidth, targetHeight);
            return Task.FromResult<ScreenCaptureFrame?>(new ScreenCaptureFrame(clock.Now(), targetWidth, targetHeight, hash, png));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or OverflowException)
        {
            return Task.FromResult<ScreenCaptureFrame?>(null);
        }
        finally
        {
            if (image != 0)
            {
                _ = X11Native.XDestroyImage(image);
            }

            if (display != 0)
            {
                _ = X11Native.XCloseDisplay(display);
            }
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class X11WindowActivitySource(IClock clock, IScreenCapture? screenCapture = null) : IWindowActivitySource
{
    public SourceKind Kind => SourceKind.WindowActivity;

    public CapabilityState Capability => OperatingSystem.IsLinux() && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"))
        ? CapabilityState.Supported(CapabilityKind.WindowActivity, "X11 EWMH _NET_ACTIVE_WINDOW + _NET_WM_PID")
        : CapabilityState.Unavailable(CapabilityKind.WindowActivity, "X11 display is unavailable");

    public Task<CapturedSample?> GetCurrentForegroundAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Capability.IsOperational)
        {
            return Task.FromResult<CapturedSample?>(null);
        }

        nint display = 0;
        nint propertyData = 0;
        nint namePtr = 0;
        try
        {
            display = X11Native.XOpenDisplay(null);
            if (display == 0)
            {
                return Task.FromResult<CapturedSample?>(null);
            }

            var root = X11Native.XDefaultRootWindow(display);
            var activeAtom = X11Native.XInternAtom(display, "_NET_ACTIVE_WINDOW", false);
            if (root == 0 || activeAtom == 0 ||
                X11Native.XGetWindowProperty(display, root, activeAtom, 0, 1, false, 0, out _, out _, out var count, out _, out propertyData) != 0 ||
                count == 0 || propertyData == 0)
            {
                return Task.FromResult<CapturedSample?>(null);
            }

            var windowValue = nint.Size == 8 ? Marshal.ReadInt64(propertyData) : Marshal.ReadInt32(propertyData);
            var window = (nint)windowValue;
            _ = X11Native.XFree(propertyData);
            propertyData = 0;
            if (window == 0)
            {
                return Task.FromResult<CapturedSample?>(null);
            }

            var title = string.Empty;
            if (X11Native.XFetchName(display, window, out namePtr) != 0 && namePtr != 0)
            {
                title = Marshal.PtrToStringUTF8(namePtr) ?? string.Empty;
                _ = X11Native.XFree(namePtr);
                namePtr = 0;
            }

            var pid = 0;
            var pidAtom = X11Native.XInternAtom(display, "_NET_WM_PID", false);
            if (pidAtom != 0 && X11Native.XGetWindowProperty(display, window, pidAtom, 0, 1, false, 0, out _, out _, out count, out _, out propertyData) == 0 &&
                count > 0 && propertyData != 0)
            {
                var pidValue = nint.Size == 8 ? Marshal.ReadInt64(propertyData) : Marshal.ReadInt32(propertyData);
                pid = pidValue > 0 && pidValue <= int.MaxValue ? (int)pidValue : 0;
                _ = X11Native.XFree(propertyData);
                propertyData = 0;
            }

            var appName = ReadProcessName(pid);
            if (string.IsNullOrWhiteSpace(title))
            {
                title = appName;
            }

            return Task.FromResult<CapturedSample?>(new CapturedSample(
                clock.Now(),
                SourceKind.WindowActivity,
                appName,
                title,
                NativeWindowHandle: window));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or IOException)
        {
            return Task.FromResult<CapturedSample?>(null);
        }
        finally
        {
            if (propertyData != 0)
            {
                _ = X11Native.XFree(propertyData);
            }

            if (namePtr != 0)
            {
                _ = X11Native.XFree(namePtr);
            }

            if (display != 0)
            {
                _ = X11Native.XCloseDisplay(display);
            }
        }
    }

    public async IAsyncEnumerable<CapturedSample> CaptureAsync(CaptureContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        string? lastSignature = null;
        var lastEmit = DateTimeOffset.MinValue;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(400));
        while (await timer.WaitForNextTickAsync(ct))
        {
            var current = await GetCurrentForegroundAsync(ct);
            if (current is null)
            {
                continue;
            }

            var signature = $"{current.AppName}\u001F{current.WindowTitle}";
            var now = clock.UtcNow;
            if (string.Equals(signature, lastSignature, StringComparison.Ordinal) && now - lastEmit < context.FallbackInterval)
            {
                continue;
            }

            lastSignature = signature;
            lastEmit = now;
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

    private static string ReadProcessName(int pid)
    {
        if (pid <= 0)
        {
            return "Unknown";
        }

        try
        {
            return File.ReadAllText($"/proc/{pid}/comm").Trim() is { Length: > 0 } name ? name : "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }
}
