using System.Buffers.Binary;

namespace AiAssistant.Voice;

/// <summary>
/// Minimal 16-bit mono PCM WAV container for opt-in command audio.
/// Keeps the encrypted blob self-describing without pulling in an audio codec dependency.
/// </summary>
internal static class CommandAudioWavEncoder
{
    private const int HeaderBytes = 44;

    public static byte[] Encode(ReadOnlySpan<float> samples, int sampleRateHz)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);

        const short channels = 1;
        const short bitsPerSample = 16;
        var dataBytes = samples.Length * sizeof(short);
        var buffer = new byte[HeaderBytes + dataBytes];

        "RIFF"u8.CopyTo(buffer);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), 36 + dataBytes);
        "WAVE"u8.CopyTo(buffer.AsSpan(8));
        "fmt "u8.CopyTo(buffer.AsSpan(12));
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(22), channels);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(24), sampleRateHz);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(28), sampleRateHz * channels * bitsPerSample / 8);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(32), (short)(channels * bitsPerSample / 8));
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(34), bitsPerSample);
        "data"u8.CopyTo(buffer.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(40), dataBytes);

        for (var i = 0; i < samples.Length; i++)
        {
            var clamped = Math.Clamp(samples[i], -1f, 1f);
            var value = (short)Math.Round(clamped * short.MaxValue, MidpointRounding.AwayFromZero);
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(HeaderBytes + (i * sizeof(short))), value);
        }

        return buffer;
    }
}
