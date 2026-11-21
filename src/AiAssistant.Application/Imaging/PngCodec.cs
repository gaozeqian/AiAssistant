using System.Buffers.Binary;
using System.IO.Compression;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Imaging;

/// <summary>
/// Dependency-free PNG codec for platform screenshots, X11 XImage frames and OCR benchmarks.
/// Supports 8-bit non-interlaced RGB/RGBA PNG input and emits RGBA PNG with no filter.
/// </summary>
public static class PngCodec
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static byte[] EncodeBgra32(ReadOnlySpan<byte> bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < checked(width * height * 4))
        {
            throw new ArgumentException("Invalid BGRA dimensions or pixel buffer.", nameof(bgra));
        }

        var rgbaStride = width * 4;
        var raw = new byte[checked((rgbaStride + 1) * height)];
        for (var y = 0; y < height; y++)
        {
            var targetRow = y * (rgbaStride + 1);
            raw[targetRow] = 0; // PNG filter None
            for (var x = 0; x < width; x++)
            {
                var src = (y * width + x) * 4;
                var dst = targetRow + 1 + x * 4;
                raw[dst] = bgra[src + 2];
                raw[dst + 1] = bgra[src + 1];
                raw[dst + 2] = bgra[src];
                raw[dst + 3] = bgra[src + 3];
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        using var output = new MemoryStream();
        output.Write(Signature);
        WriteChunk(output, "IHDR"u8.ToArray(), BuildIhdr(width, height));
        WriteChunk(output, "IDAT"u8.ToArray(), compressed.ToArray());
        WriteChunk(output, "IEND"u8.ToArray(), []);
        return output.ToArray();
    }

    public static bool TryDecodeToBgra32(
        ReadOnlySpan<byte> png,
        out byte[]? bgra,
        out int width,
        out int height,
        out ulong perceptualHash)
    {
        bgra = null;
        width = 0;
        height = 0;
        perceptualHash = 0;
        try
        {
            if (png.Length < 45 || !png[..8].SequenceEqual(Signature))
            {
                return false;
            }

            width = BinaryPrimitives.ReadInt32BigEndian(png.Slice(16, 4));
            height = BinaryPrimitives.ReadInt32BigEndian(png.Slice(20, 4));
            var bitDepth = png[24];
            var colorType = png[25];
            var interlace = png[28];
            if (width <= 0 || height <= 0 || width > 8192 || height > 8192 ||
                bitDepth != 8 || colorType is not (2 or 6) || interlace != 0 || png[26] != 0 || png[27] != 0)
            {
                return false;
            }

            var channels = colorType == 6 ? 4 : 3;
            var sourceStride = checked(width * channels);
            var expectedRawBytes = checked((sourceStride + 1) * height);
            using var compressed = new MemoryStream();
            var offset = 8;
            var sawEnd = false;
            while (offset + 12 <= png.Length)
            {
                var length = BinaryPrimitives.ReadInt32BigEndian(png.Slice(offset, 4));
                if (length < 0 || offset + 12L + length > png.Length)
                {
                    return false;
                }

                var type = png.Slice(offset + 4, 4);
                var data = png.Slice(offset + 8, length);
                if (type.SequenceEqual("IDAT"u8))
                {
                    compressed.Write(data);
                }
                else if (type.SequenceEqual("IEND"u8))
                {
                    sawEnd = true;
                    break;
                }

                offset += 12 + length;
            }

            if (!sawEnd || compressed.Length == 0)
            {
                return false;
            }

            compressed.Position = 0;
            var filtered = new byte[expectedRawBytes];
            using (var zlib = new ZLibStream(compressed, CompressionMode.Decompress))
            {
                zlib.ReadExactly(filtered);
                if (zlib.ReadByte() != -1)
                {
                    return false;
                }
            }

            var rgba = new byte[checked(width * height * 4)];
            // PNG filters reference reconstructed neighbours, never the filtered stream bytes.
            var previousRow = new byte[sourceStride];
            var currentRow = new byte[sourceStride];
            for (var y = 0; y < height; y++)
            {
                var filter = filtered[y * (sourceStride + 1)];
                if (filter > 4)
                {
                    return false;
                }

                var sourceRow = y * (sourceStride + 1) + 1;
                for (var x = 0; x < sourceStride; x++)
                {
                    var raw = filtered[sourceRow + x];
                    var left = x >= channels ? currentRow[x - channels] : 0;
                    var up = y > 0 ? previousRow[x] : 0;
                    var upLeft = y > 0 && x >= channels ? previousRow[x - channels] : 0;
                    currentRow[x] = filter switch
                    {
                        0 => raw,
                        1 => unchecked((byte)(raw + left)),
                        2 => unchecked((byte)(raw + up)),
                        3 => unchecked((byte)(raw + ((left + up) >> 1))),
                        4 => unchecked((byte)(raw + Paeth(left, up, upLeft))),
                        _ => raw
                    };
                }

                var rowBase = y * width * 4;
                for (var px = 0; px < width; px++)
                {
                    var source = px * channels;
                    var target = rowBase + px * 4;
                    rgba[target] = currentRow[source + 2];
                    rgba[target + 1] = currentRow[source + 1];
                    rgba[target + 2] = currentRow[source];
                    rgba[target + 3] = channels == 4 ? currentRow[source + 3] : (byte)255;
                }

                (previousRow, currentRow) = (currentRow, previousRow);
            }

            bgra = rgba;
            perceptualHash = PerceptualHash.ComputeFromBgra32(rgba, width, height, width * 4);
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or OverflowException or ArgumentException or IndexOutOfRangeException)
        {
            bgra = null;
            return false;
        }
    }

    private static byte[] BuildIhdr(int width, int height)
    {
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        return ihdr;
    }

    private static void WriteChunk(Stream stream, byte[] type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        stream.Write(type);
        stream.Write(data);
        var crc = Crc32(type, data);
        BinaryPrimitives.WriteUInt32BigEndian(length, crc);
        stream.Write(length);
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in type.Concat(data))
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
