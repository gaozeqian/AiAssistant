namespace AiAssistant.Platform.Linux.PipeWire;

/// <summary>
/// Converts a mapped PipeWire video buffer into tightly packed BGRA32 rows.
/// Supports the RGB formats requested from the portal; anything else is rejected so the
/// caller can fall back to a portal screenshot instead of persisting garbled pixels.
/// </summary>
public static class PipeWireFrameConverter
{
    public static bool TryConvertToBgra32(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        int stride,
        int videoFormat,
        out byte[]? bgra)
    {
        bgra = null;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        var rowBytes = width * 4;
        var absoluteStride = Math.Abs(stride);
        if (absoluteStride == 0)
        {
            absoluteStride = rowBytes;
        }

        if (absoluteStride < rowBytes ||
            source.Length < ((long)(height - 1) * absoluteStride) + rowBytes)
        {
            return false;
        }

        var output = new byte[(long)rowBytes * height];
        for (var y = 0; y < height; y++)
        {
            var sourceRow = stride < 0 ? (height - 1 - y) * absoluteStride : y * absoluteStride;
            var input = source.Slice(sourceRow, rowBytes);
            var target = output.AsSpan(y * rowBytes, rowBytes);
            if (!ConvertRow(input, target, videoFormat))
            {
                return false;
            }
        }

        bgra = output;
        return true;
    }

    private static bool ConvertRow(ReadOnlySpan<byte> input, Span<byte> output, int videoFormat)
    {
        switch (videoFormat)
        {
            case PipeWireNative.VideoFormatBgra:
                input.CopyTo(output);
                return true;
            case PipeWireNative.VideoFormatBgrx:
                CopyWithOpaqueAlpha(input, output, redOffset: 2, greenOffset: 1, blueOffset: 0);
                return true;
            case PipeWireNative.VideoFormatRgba:
                CopyWithOpaqueAlpha(input, output, redOffset: 0, greenOffset: 1, blueOffset: 2);
                return true;
            case PipeWireNative.VideoFormatRgbx:
                CopyWithOpaqueAlpha(input, output, redOffset: 0, greenOffset: 1, blueOffset: 2);
                return true;
            case PipeWireNative.VideoFormatXbgr:
                CopyWithOpaqueAlpha(input, output, redOffset: 3, greenOffset: 2, blueOffset: 1);
                return true;
            case PipeWireNative.VideoFormatXrgb:
                CopyWithOpaqueAlpha(input, output, redOffset: 1, greenOffset: 2, blueOffset: 3);
                return true;
            case PipeWireNative.VideoFormatArgb:
                CopyWithAlpha(input, output, alphaOffset: 0, redOffset: 1, greenOffset: 2, blueOffset: 3);
                return true;
            case PipeWireNative.VideoFormatAbgr:
                CopyWithAlpha(input, output, alphaOffset: 0, redOffset: 3, greenOffset: 2, blueOffset: 1);
                return true;
            default:
                return false;
        }
    }

    private static void CopyWithOpaqueAlpha(
        ReadOnlySpan<byte> input,
        Span<byte> output,
        int redOffset,
        int greenOffset,
        int blueOffset)
    {
        for (var x = 0; x < output.Length; x += 4)
        {
            output[x] = input[x + blueOffset];
            output[x + 1] = input[x + greenOffset];
            output[x + 2] = input[x + redOffset];
            output[x + 3] = 255;
        }
    }

    private static void CopyWithAlpha(
        ReadOnlySpan<byte> input,
        Span<byte> output,
        int alphaOffset,
        int redOffset,
        int greenOffset,
        int blueOffset)
    {
        for (var x = 0; x < output.Length; x += 4)
        {
            output[x] = input[x + blueOffset];
            output[x + 1] = input[x + greenOffset];
            output[x + 2] = input[x + redOffset];
            output[x + 3] = input[x + alphaOffset];
        }
    }
}
