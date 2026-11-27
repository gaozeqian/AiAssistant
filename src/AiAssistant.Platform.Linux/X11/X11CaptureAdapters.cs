using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AiAssistant.Application.Imaging;
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
    /// <summary>
    /// Converts an XImage buffer to BGRA32, averaging (not sampling) the source pixels behind each
    /// destination pixel so downscaled text stays legible.
    /// </summary>
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
        var blue = new ChannelMask(blueMask);
        var green = new ChannelMask(greenMask);
        var red = new ChannelMask(redMask);
        var output = new byte[checked(targetWidth * targetHeight * 4)];

        // Box filter: every destination pixel averages the full source rectangle it covers. Point
        // sampling (the previous implementation) discarded source pixels instead of averaging them,
        // which is what made 2560px displays unreadable once downscaled to the stored frame width.
        for (var y = 0; y < targetHeight; y++)
        {
            var sourceY0 = Math.Min(sourceHeight - 1, y * sourceHeight / targetHeight);
            var sourceY1 = Math.Clamp((y + 1) * sourceHeight / targetHeight, sourceY0 + 1, sourceHeight);
            for (var x = 0; x < targetWidth; x++)
            {
                var sourceX0 = Math.Min(sourceWidth - 1, x * sourceWidth / targetWidth);
                var sourceX1 = Math.Clamp((x + 1) * sourceWidth / targetWidth, sourceX0 + 1, sourceWidth);

                // ulong accumulators: a 1px target from a 16384px source would overflow uint.
                ulong sumBlue = 0;
                ulong sumGreen = 0;
                ulong sumRed = 0;
                for (var sy = sourceY0; sy < sourceY1; sy++)
                {
                    var rowStart = sy * stride;
                    for (var sx = sourceX0; sx < sourceX1; sx++)
                    {
                        var pixel = ReadPixel(source, rowStart + sx * bytesPerPixel, bytesPerPixel, byteOrder);
                        sumBlue += blue.Extract(pixel);
                        sumGreen += green.Extract(pixel);
                        sumRed += red.Extract(pixel);
                    }
                }

                var sampleCount = (ulong)((sourceY1 - sourceY0) * (sourceX1 - sourceX0));
                var target = (y * targetWidth + x) * 4;
                output[target] = (byte)(sumBlue / sampleCount);
                output[target + 1] = (byte)(sumGreen / sampleCount);
                output[target + 2] = (byte)(sumRed / sampleCount);
                output[target + 3] = 255;
            }
        }

        return output;
    }

    /// <summary>Reads one packed XImage pixel honouring the buffer byte order and pixel width.</summary>
    private static uint ReadPixel(ReadOnlySpan<byte> source, int index, int bytesPerPixel, int byteOrder)
    {
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

        return pixel;
    }

    /// <summary>
    /// Extracts one 8-bit channel from a packed pixel. Mask geometry is resolved once per frame instead
    /// of once per pixel, and the common 8-bit case skips the rescale division entirely.
    /// </summary>
    private readonly struct ChannelMask(ulong mask)
    {
        private readonly ulong _mask = mask;
        private readonly int _shift = mask == 0 ? 0 : BitOperations.TrailingZeroCount(mask);
        private readonly ulong _max = mask == 0 ? 0 : mask >> BitOperations.TrailingZeroCount(mask);

        public byte Extract(uint pixel)
        {
            if (_max == 0)
            {
                return 0;
            }

            var value = (pixel & _mask) >> _shift;
            return _max == 255 ? (byte)value : (byte)Math.Min(255UL, value * 255UL / _max);
        }
    }
}

public sealed class X11ScreenCapture(IClock clock) : IScreenCapture
{
    public CapabilityState Capability => OperatingSystem.IsLinux() && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"))
        ? CapabilityState.Supported(CapabilityKind.ScreenCapture, "X11 XGetImage 截图（PNG + 感知哈希）")
        : CapabilityState.Unavailable(CapabilityKind.ScreenCapture, "X11 显示不可用");

    public Task<IReadOnlyList<ScreenCaptureFrame>> CaptureDisplaysAsync(ScreenCaptureTarget target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Capability.IsOperational)
        {
            return Task.FromResult<IReadOnlyList<ScreenCaptureFrame>>([]);
        }

        nint display = 0;
        nint image = 0;
        try
        {
            display = X11Native.XOpenDisplay(null);
            if (display == 0)
            {
                return Task.FromResult<IReadOnlyList<ScreenCaptureFrame>>([]);
            }

            var root = X11Native.XDefaultRootWindow(display);
            var width = X11Native.XDisplayWidth(display, 0);
            var height = X11Native.XDisplayHeight(display, 0);
            if (root == 0 || width <= 0 || height <= 0 || width > 16384 || height > 16384)
            {
                return Task.FromResult<IReadOnlyList<ScreenCaptureFrame>>([]);
            }

            image = X11Native.XGetImage(display, root, 0, 0, (uint)width, (uint)height, X11Native.AllPlanes, X11Native.ZPixmap);
            if (image == 0)
            {
                return Task.FromResult<IReadOnlyList<ScreenCaptureFrame>>([]);
            }

            var layout = Marshal.PtrToStructure<X11Native.XImageLayout>(image);
            if (layout.Data == 0 || layout.BytesPerLine <= 0 || layout.BitsPerPixel is not (24 or 32))
            {
                return Task.FromResult<IReadOnlyList<ScreenCaptureFrame>>([]);
            }

            var byteCount = checked(layout.BytesPerLine * layout.Height);
            if (byteCount <= 0 || byteCount > 512 * 1024 * 1024)
            {
                return Task.FromResult<IReadOnlyList<ScreenCaptureFrame>>([]);
            }

            var raw = new byte[byteCount];
            Marshal.Copy(layout.Data, raw, 0, byteCount);
            var targetWidth = Math.Min(width, ScreenCaptureLimits.MaxFrameWidth);
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
            // The X11 root window spans every monitor (Xinerama/RandR), so one composite frame covers them all.
            return Task.FromResult<IReadOnlyList<ScreenCaptureFrame>>(
                [new ScreenCaptureFrame(clock.Now(), targetWidth, targetHeight, hash, png, IsForegroundDisplay: true)]);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or OverflowException)
        {
            return Task.FromResult<IReadOnlyList<ScreenCaptureFrame>>([]);
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

internal sealed class X11WindowActivitySource(IClock clock, IScreenCapture? screenCapture = null) : IWindowActivitySource
{
    public SourceKind Kind => SourceKind.WindowActivity;

    public CapabilityState Capability => OperatingSystem.IsLinux() && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"))
        ? CapabilityState.Supported(CapabilityKind.WindowActivity, "X11 EWMH 前台窗口监听")
        : CapabilityState.Unavailable(CapabilityKind.WindowActivity, "X11 显示不可用");

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
