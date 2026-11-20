using System.Buffers.Binary;
using System.IO.Compression;

namespace AiAssistant.Platform.Windows;

/// <summary>
/// Zero-dependency managed PNG encoder converting BGRA32 buffers into standard RGBA PNG bytes.
/// Cross-platform compatible with Avalonia Bitmap without native imaging library conflicts.
/// </summary>
public static class PngEncoder
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrc32Table();

    public static byte[] EncodeBgra32(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (bgra.Length < stride * height)
        {
            throw new ArgumentException("BGRA buffer is smaller than stride * height.", nameof(bgra));
        }

        using var output = new MemoryStream();
        output.Write(PngSignature);

        // IHDR: width(4), height(4), bitDepth=8, colorType=6 (RGBA), compression=0, filter=0, interlace=0
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[..4], (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.Slice(4, 4), (uint)height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        WriteChunk(output, "IHDR"u8, ihdr);

        // Compress scanlines (each row starts with filter byte 0 + RGBA pixels)
        byte[] idatPayload;
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                var rowBuffer = new byte[1 + (width * 4)];
                for (var y = 0; y < height; y++)
                {
                    rowBuffer[0] = 0; // Filter: None
                    var srcRow = bgra.Slice(y * stride, width * 4);
                    for (var x = 0; x < width; x++)
                    {
                        var srcIdx = x * 4;
                        var dstIdx = 1 + srcIdx;
                        rowBuffer[dstIdx] = srcRow[srcIdx + 2];     // R
                        rowBuffer[dstIdx + 1] = srcRow[srcIdx + 1]; // G
                        rowBuffer[dstIdx + 2] = srcRow[srcIdx];     // B
                        rowBuffer[dstIdx + 3] = 255;                // Opaque Alpha
                    }

                    zlib.Write(rowBuffer, 0, rowBuffer.Length);
                }
            }

            idatPayload = compressed.ToArray();
        }

        WriteChunk(output, "IDAT"u8, idatPayload);
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> chunkType, ReadOnlySpan<byte> data)
    {
        Span<byte> lengthBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lengthBytes, (uint)data.Length);
        output.Write(lengthBytes);
        output.Write(chunkType);
        if (!data.IsEmpty)
        {
            output.Write(data);
        }

        var crc = 0xFFFFFFFFu;
        crc = UpdateCrc32(crc, chunkType);
        crc = UpdateCrc32(crc, data);
        crc ^= 0xFFFFFFFFu;

        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static uint UpdateCrc32(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
