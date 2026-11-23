using System.Runtime.InteropServices;

namespace AiAssistant.Platform.Linux.PipeWire;

/// <summary>
/// Turns PipeWire format pods and spa_buffer memory into BGRA32 frames. Deliberately free of
/// native stream calls, listener registration, and waiter/slot management: those belong to
/// <see cref="PipeWireScreenCastStream"/>. Out parameters report the exact skip reason so the
/// caller can preserve its existing error-state and logging behaviour.
/// </summary>
internal sealed class PipeWireFrameProcessor
{
    private readonly TimeSpan _minFrameInterval;
    private DateTimeOffset _lastCopiedAt = DateTimeOffset.MinValue;

    public PipeWireFrameProcessor(TimeSpan minFrameInterval)
    {
        _minFrameInterval = minFrameInterval;
    }

    /// <summary>
    /// Parses a pw_param format pod. Returns false for a malformed pod (no error) or an
    /// unsupported format (<paramref name="error"/> set).
    /// </summary>
    public static bool TryParseNegotiatedFormat(
        IntPtr param,
        out int format,
        out int width,
        out int height,
        out string? error)
    {
        format = 0;
        width = 0;
        height = 0;
        error = null;

        var size = Marshal.ReadInt32(param);
        if (size <= 0 || size > 8192)
        {
            return false;
        }

        var bytes = new byte[8 + size];
        Marshal.Copy(param, bytes, 0, bytes.Length);
        if (!SpaPodCodec.TryParseVideoFormat(bytes, out format, out width, out height))
        {
            error = "unsupported negotiated video format";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Reads one spa_buffer and converts it to BGRA32. Returns false for rate limiting, empty or
    /// invalid buffers (no error), and unsupported pixel layouts (<paramref name="error"/> set).
    /// </summary>
    public bool TryCreateFrame(
        IntPtr bufferPointer,
        int negotiatedWidth,
        int negotiatedHeight,
        int negotiatedFormat,
        DateTimeOffset now,
        out PipeWireFrame? frame,
        out string? error)
    {
        frame = null;
        error = null;

        if (now - _lastCopiedAt < _minFrameInterval)
        {
            return false;
        }

        var spaBufferPointer = Marshal.ReadIntPtr(bufferPointer);
        if (spaBufferPointer == IntPtr.Zero)
        {
            return false;
        }

        var spaBuffer = Marshal.PtrToStructure<PipeWireNative.SpaBuffer>(spaBufferPointer);
        if (spaBuffer.DataCount == 0 || spaBuffer.Datas == IntPtr.Zero)
        {
            return false;
        }

        // spa_buffer.datas points at the first spa_data struct (not an array of pointers).
        var spaData = Marshal.PtrToStructure<PipeWireNative.SpaData>(spaBuffer.Datas);
        if (spaData.Data == IntPtr.Zero || spaData.Chunk == IntPtr.Zero)
        {
            return false;
        }

        var chunk = Marshal.PtrToStructure<PipeWireNative.SpaChunk>(spaData.Chunk);
        var width = negotiatedWidth;
        var height = negotiatedHeight;
        var format = negotiatedFormat;
        if (chunk.Size == 0 || width <= 0 || height <= 0 || format == 0)
        {
            return false;
        }

        var size = (int)Math.Min(chunk.Size, spaData.MaxSize == 0 ? chunk.Size : spaData.MaxSize);
        var source = new byte[size];
        Marshal.Copy(IntPtr.Add(spaData.Data, (int)chunk.Offset), source, 0, size);
        if (!PipeWireFrameConverter.TryConvertToBgra32(source, width, height, chunk.Stride, format, out var bgra) ||
            bgra is null)
        {
            error = "unsupported pixel layout";
            return false;
        }

        frame = new PipeWireFrame(width, height, bgra, now);
        _lastCopiedAt = now;
        return true;
    }
}
