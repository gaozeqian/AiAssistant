using System.Buffers.Binary;

namespace AiAssistant.Platform.Linux.PipeWire;

/// <summary>
/// Hand-rolled SPA pod codec for the single object we need: a video EnumFormat request
/// (mediaType/mediaSubtype/format/size) and the negotiated Format reply.
/// Layout follows spa/pod/pod.h; pods and properties are 8-byte aligned.
/// </summary>
public static class SpaPodCodec
{
    /// <summary>SPA_VIDEO_FORMAT_BGRA; also the preferred negotiation target.</summary>
    public const int VideoFormatBgra = PipeWireNative.VideoFormatBgra;

    /// <summary>SPA_VIDEO_FORMAT_RGBA.</summary>
    public const int VideoFormatRgba = PipeWireNative.VideoFormatRgba;

    private const int Alignment = 8;

    public static byte[] BuildVideoFormatRequest(
        IReadOnlyList<int> formats,
        int maxWidth,
        int maxHeight)
    {
        ArgumentNullException.ThrowIfNull(formats);
        if (formats.Count == 0)
        {
            throw new ArgumentException("At least one video format must be requested.", nameof(formats));
        }

        using var buffer = new MemoryStream(192);
        WriteUInt32(buffer, 0); // size placeholder
        WriteUInt32(buffer, PipeWireNative.SpaTypeObject);
        WriteUInt32(buffer, PipeWireNative.SpaTypeObjectFormat);
        WriteUInt32(buffer, PipeWireNative.SpaParamEnumFormat);

        WriteIdProperty(buffer, PipeWireNative.FormatKeyMediaType, PipeWireNative.MediaTypeVideo);
        WriteIdProperty(buffer, PipeWireNative.FormatKeyMediaSubtype, PipeWireNative.MediaSubtypeRaw);
        WriteChoiceIdProperty(buffer, PipeWireNative.VideoKeyFormat, formats);
        WriteChoiceRectangleProperty(
            buffer,
            PipeWireNative.VideoKeySize,
            defaultWidth: 0,
            defaultHeight: 0,
            minWidth: 1,
            minHeight: 1,
            maxWidth: Math.Max(1, maxWidth),
            maxHeight: Math.Max(1, maxHeight));

        var bytes = buffer.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)(bytes.Length - 8));
        return bytes;
    }

    /// <summary>Reads the negotiated video format and size from a Format pod.</summary>
    public static bool TryParseVideoFormat(ReadOnlySpan<byte> pod, out int format, out int width, out int height)
    {
        format = 0;
        width = 0;
        height = 0;
        if (pod.Length < 16)
        {
            return false;
        }

        var size = BinaryPrimitives.ReadUInt32LittleEndian(pod);
        var type = BinaryPrimitives.ReadUInt32LittleEndian(pod[4..]);
        if (type != PipeWireNative.SpaTypeObject || size < 8 || 8 + (long)size > pod.Length)
        {
            return false;
        }

        var end = 8 + (int)size;
        var offset = 16;
        while (offset + 8 <= end)
        {
            var key = BinaryPrimitives.ReadUInt32LittleEndian(pod[offset..]);
            var valueOffset = offset + 8;
            var valueSize = BinaryPrimitives.ReadUInt32LittleEndian(pod[valueOffset..]);
            var valueType = BinaryPrimitives.ReadUInt32LittleEndian(pod[(valueOffset + 4)..]);
            if (valueOffset + 8 + (long)valueSize > pod.Length)
            {
                return false;
            }

            var value = pod.Slice(valueOffset + 8, (int)valueSize);
            if (key == PipeWireNative.VideoKeyFormat && TryReadVideoFormatValue((int)valueType, value, out var parsedFormat))
            {
                format = parsedFormat;
            }
            else if (key == PipeWireNative.VideoKeySize && TryReadRectangleValue((int)valueType, value, out var parsedWidth, out var parsedHeight))
            {
                width = parsedWidth;
                height = parsedHeight;
            }

            offset = Align(valueOffset + 8 + (int)valueSize);
        }

        return format != 0;
    }

    private static bool TryReadVideoFormatValue(int valueType, ReadOnlySpan<byte> value, out int format)
    {
        format = 0;
        if (valueType == PipeWireNative.SpaTypeId)
        {
            format = (int)BinaryPrimitives.ReadUInt32LittleEndian(value);
            return true;
        }

        if (valueType == PipeWireNative.SpaTypeChoice && value.Length >= 16)
        {
            format = (int)BinaryPrimitives.ReadUInt32LittleEndian(value[16..]);
            return true;
        }

        return false;
    }

    private static bool TryReadRectangleValue(
        int valueType,
        ReadOnlySpan<byte> value,
        out int width,
        out int height)
    {
        width = 0;
        height = 0;
        if (valueType == PipeWireNative.SpaTypeRectangle && value.Length >= 8)
        {
            width = BinaryPrimitives.ReadInt32LittleEndian(value);
            height = BinaryPrimitives.ReadInt32LittleEndian(value[4..]);
            return width > 0 && height > 0;
        }

        if (valueType == PipeWireNative.SpaTypeChoice && value.Length >= 24)
        {
            width = BinaryPrimitives.ReadInt32LittleEndian(value[16..]);
            height = BinaryPrimitives.ReadInt32LittleEndian(value[20..]);
            return width > 0 && height > 0;
        }

        return false;
    }

    private static void WriteIdProperty(MemoryStream buffer, uint key, uint value)
    {
        WriteUInt32(buffer, key);
        WriteUInt32(buffer, 0);
        WriteUInt32(buffer, 4);
        WriteUInt32(buffer, PipeWireNative.SpaTypeId);
        WriteUInt32(buffer, value);
        Align(buffer);
    }

    private static void WriteChoiceIdProperty(MemoryStream buffer, uint key, IReadOnlyList<int> values)
    {
        WriteUInt32(buffer, key);
        WriteUInt32(buffer, 0);
        WriteUInt32(buffer, (uint)(16 + (values.Count * 4)));
        WriteUInt32(buffer, PipeWireNative.SpaTypeChoice);
        WriteUInt32(buffer, PipeWireNative.ChoiceEnum);
        WriteUInt32(buffer, 0);
        WriteUInt32(buffer, 4);
        WriteUInt32(buffer, PipeWireNative.SpaTypeId);
        foreach (var value in values)
        {
            WriteUInt32(buffer, (uint)value);
        }

        Align(buffer);
    }

    private static void WriteChoiceRectangleProperty(
        MemoryStream buffer,
        uint key,
        int defaultWidth,
        int defaultHeight,
        int minWidth,
        int minHeight,
        int maxWidth,
        int maxHeight)
    {
        WriteUInt32(buffer, key);
        WriteUInt32(buffer, 0);
        WriteUInt32(buffer, 16 + (3 * 8));
        WriteUInt32(buffer, PipeWireNative.SpaTypeChoice);
        WriteUInt32(buffer, PipeWireNative.ChoiceRange);
        WriteUInt32(buffer, 0);
        WriteUInt32(buffer, 8);
        WriteUInt32(buffer, PipeWireNative.SpaTypeRectangle);
        WriteRectangle(buffer, defaultWidth, defaultHeight);
        WriteRectangle(buffer, minWidth, minHeight);
        WriteRectangle(buffer, maxWidth, maxHeight);
        Align(buffer);
    }

    private static void WriteRectangle(MemoryStream buffer, int width, int height)
    {
        WriteUInt32(buffer, (uint)width);
        WriteUInt32(buffer, (uint)height);
    }

    private static void WriteUInt32(MemoryStream buffer, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        buffer.Write(bytes);
    }

    private static void Align(MemoryStream buffer)
    {
        while (buffer.Length % Alignment != 0)
        {
            buffer.WriteByte(0);
        }
    }

    private static int Align(int offset) => (offset + (Alignment - 1)) & ~(Alignment - 1);
}
