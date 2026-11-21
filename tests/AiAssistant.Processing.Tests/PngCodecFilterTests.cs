using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using AiAssistant.Application.Imaging;

namespace AiAssistant.Processing.Tests;

/// <summary>
/// Regression coverage for PNG row filters: real screenshots use Sub/Up/Average/Paeth, and
/// reconstructing them with unfiltered neighbour bytes corrupts the image (S4-R2 field bug).
/// </summary>
public sealed class PngCodecFilterTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void DecodeReconstructsEveryFilterType(int filter)
    {
        const int width = 3;
        const int height = 2;
        var rgba = new byte[width * height * 4];
        for (var i = 0; i < rgba.Length; i++)
        {
            rgba[i] = (byte)(i * 7 + 3);
        }

        // Make the alpha channel opaque so the round trip is unambiguous.
        for (var p = 0; p < width * height; p++)
        {
            rgba[p * 4 + 3] = 0xFF;
        }

        var png = BuildPng(width, height, rgba, _ => filter);

        Assert.True(PngCodec.TryDecodeToBgra32(png, out var bgra, out var decodedWidth, out var decodedHeight, out _));
        Assert.Equal(width, decodedWidth);
        Assert.Equal(height, decodedHeight);
        Assert.NotNull(bgra);

        for (var p = 0; p < width * height; p++)
        {
            Assert.Equal(rgba[p * 4 + 2], bgra![p * 4]);     // B
            Assert.Equal(rgba[p * 4 + 1], bgra[p * 4 + 1]);  // G
            Assert.Equal(rgba[p * 4], bgra[p * 4 + 2]);      // R
            Assert.Equal(0xFF, bgra[p * 4 + 3]);             // A
        }
    }

    [Fact]
    public void DecodeHandlesMixedFiltersAcrossRows()
    {
        const int width = 2;
        const int height = 5;
        var rgba = new byte[width * height * 4];
        for (var i = 0; i < rgba.Length; i++)
        {
            rgba[i] = (byte)(255 - i * 3);
        }

        var png = BuildPng(width, height, rgba, row => row % 5);

        Assert.True(PngCodec.TryDecodeToBgra32(png, out var bgra, out _, out _, out _));
        for (var p = 0; p < width * height; p++)
        {
            Assert.Equal(rgba[p * 4 + 2], bgra![p * 4]);
            Assert.Equal(rgba[p * 4], bgra[p * 4 + 2]);
        }
    }

    private static byte[] BuildPng(int width, int height, byte[] rgba, Func<int, int> filterForRow)
    {
        const int channels = 4;
        var stride = width * channels;
        var raw = new byte[(stride + 1) * height];
        for (var y = 0; y < height; y++)
        {
            var filter = filterForRow(y);
            raw[y * (stride + 1)] = (byte)filter;
            for (var x = 0; x < stride; x++)
            {
                var value = rgba[y * stride + x];
                var left = x >= channels ? rgba[y * stride + x - channels] : 0;
                var up = y > 0 ? rgba[(y - 1) * stride + x] : 0;
                var upLeft = y > 0 && x >= channels ? rgba[(y - 1) * stride + x - channels] : 0;
                var predictor = filter switch
                {
                    1 => left,
                    2 => up,
                    3 => (left + up) >> 1,
                    4 => Paeth(left, up, upLeft),
                    _ => 0
                };
                raw[y * (stride + 1) + 1 + x] = unchecked((byte)(value - predictor));
            }
        }

        byte[] compressed;
        using (var buffer = new MemoryStream())
        {
            using (var zlib = new ZLibStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
            {
                zlib.Write(raw);
            }

            compressed = buffer.ToArray();
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        WriteChunk(png, "IHDR"u8.ToArray(), ihdr);
        WriteChunk(png, "IDAT"u8.ToArray(), compressed);
        WriteChunk(png, "IEND"u8.ToArray(), []);
        return png.ToArray();
    }

    private static int Paeth(int left, int up, int upLeft)
    {
        var estimate = left + up - upLeft;
        var leftDistance = Math.Abs(estimate - left);
        var upDistance = Math.Abs(estimate - up);
        var upLeftDistance = Math.Abs(estimate - upLeft);
        if (leftDistance <= upDistance && leftDistance <= upLeftDistance)
        {
            return left;
        }

        return upDistance <= upLeftDistance ? up : upLeft;
    }

    private static void WriteChunk(Stream stream, byte[] type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        stream.Write(type);
        stream.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(length, Crc32(type, data));
        stream.Write(length);
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in type.Concat(data))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}
