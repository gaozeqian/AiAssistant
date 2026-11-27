using System.Buffers.Binary;

namespace AiAssistant.Voice;

/// <summary>
/// Decodes the 16-bit mono PCM WAV blobs written by <see cref="CommandAudioWavEncoder"/> so the
/// voice page can replay a recorded command without an audio codec dependency.
/// Chunk walking keeps it tolerant of extra chunks (LIST/fact) in files written by other tools.
/// </summary>
public static class CommandAudioWavDecoder
{
    private const short PcmFormat = 1;

    public static bool TryDecode(ReadOnlySpan<byte> wav, out float[] samples, out int sampleRateHz)
    {
        samples = [];
        sampleRateHz = 0;
        if (wav.Length < 12 ||
            !wav[..4].SequenceEqual("RIFF"u8) ||
            !wav[8..12].SequenceEqual("WAVE"u8))
        {
            return false;
        }

        var offset = 12;
        var channels = 0;
        var bitsPerSample = 0;
        ReadOnlySpan<byte> data = default;
        while (offset + 8 <= wav.Length)
        {
            var chunkId = wav.Slice(offset, 4);
            var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(wav.Slice(offset + 4, 4));
            var payloadStart = offset + 8;
            if (chunkSize < 0 || payloadStart + chunkSize > wav.Length)
            {
                return false;
            }

            var payload = wav.Slice(payloadStart, chunkSize);
            if (chunkId.SequenceEqual("fmt "u8) && chunkSize >= 16)
            {
                var format = BinaryPrimitives.ReadInt16LittleEndian(payload);
                if (format != PcmFormat)
                {
                    return false;
                }

                channels = BinaryPrimitives.ReadInt16LittleEndian(payload[2..]);
                sampleRateHz = BinaryPrimitives.ReadInt32LittleEndian(payload[4..]);
                bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(payload[14..]);
            }
            else if (chunkId.SequenceEqual("data"u8))
            {
                data = payload;
            }

            // Chunks are word-aligned.
            offset = payloadStart + chunkSize + (chunkSize % 2);
        }

        if (channels != 1 || bitsPerSample != 16 || sampleRateHz <= 0 || data.Length < sizeof(short))
        {
            return false;
        }

        var frameCount = data.Length / sizeof(short);
        var decoded = new float[frameCount];
        for (var i = 0; i < frameCount; i++)
        {
            decoded[i] = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(i * sizeof(short), sizeof(short))) / 32768f;
        }

        samples = decoded;
        return true;
    }
}
