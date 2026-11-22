using System.Numerics;

namespace AiAssistant.Core.ValueObjects;

/// <summary>
/// 64-bit perceptual difference hash (dHash/pHash) with Hamming distance comparison (design.md §9: distance &lt;= 5 = duplicate).
/// </summary>
public static class PerceptualHash
{
    public const int DefaultDuplicateThreshold = 5;

    public static int HammingDistance(ulong a, ulong b) =>
        BitOperations.PopCount(a ^ b);

    public static bool IsNearDuplicate(ulong a, ulong b, int maxDistance = DefaultDuplicateThreshold) =>
        HammingDistance(a, b) <= maxDistance;

    /// <summary>
    /// Computes a 64-bit horizontal gradient hash from an 8x9 grayscale grid or raw BGRA32 buffer.
    /// </summary>
    public static ulong ComputeFromBgra32(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        if (width <= 1 || height <= 0 || bgra.Length < stride * height)
        {
            return 0UL;
        }

        Span<byte> grid = stackalloc byte[9 * 8];
        for (var row = 0; row < 8; row++)
        {
            var srcY = Math.Min(height - 1, (row * height) / 8);
            for (var col = 0; col < 9; col++)
            {
                var srcX = Math.Min(width - 1, (col * width) / 9);
                var idx = (srcY * stride) + (srcX * 4);
                var b = bgra[idx];
                var g = bgra[idx + 1];
                var r = bgra[idx + 2];
                grid[(row * 9) + col] = (byte)(((r * 77) + (g * 150) + (b * 29)) >> 8);
            }
        }

        var hash = 0UL;
        var bit = 0;
        for (var row = 0; row < 8; row++)
        {
            for (var col = 0; col < 8; col++)
            {
                if (grid[(row * 9) + col] > grid[(row * 9) + col + 1])
                {
                    hash |= 1UL << bit;
                }

                bit++;
            }
        }

        return hash;
    }
}
