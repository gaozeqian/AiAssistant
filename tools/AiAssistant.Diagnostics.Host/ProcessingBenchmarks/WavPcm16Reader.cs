using System.Buffers.Binary;

namespace AiAssistant.Processing.Benchmarks;

public sealed record PcmAudio(ReadOnlyMemory<float> Samples, int SampleRateHz);

/// <summary>
/// Minimal RIFF/WAVE reader for 16-bit PCM golden-set audio (S4-R).
/// Accepts mono or stereo at any sample rate and linearly resamples to 16 kHz, which is what
/// the sherpa-onnx and Whisper adapters expect.
/// </summary>
public static class WavPcm16Reader
{
    public const int TargetSampleRateHz = 16_000;

    public static PcmAudio ReadAs16kMono(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 44 ||
            !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
            !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("not a RIFF/WAVE file");
        }

        var channels = 0;
        var sampleRate = 0;
        var bitsPerSample = 0;
        ReadOnlySpan<byte> data = default;

        var offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            var chunkId = bytes.AsSpan(offset, 4);
            var chunkSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            var payloadStart = offset + 8;
            if (chunkSize < 0 || payloadStart + chunkSize > bytes.Length)
            {
                chunkSize = Math.Max(0, bytes.Length - payloadStart);
            }

            if (chunkId.SequenceEqual("fmt "u8) && chunkSize >= 16)
            {
                var format = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(payloadStart, 2));
                if (format != 1)
                {
                    throw new InvalidDataException($"unsupported WAV format code {format}; expected PCM (1)");
                }

                channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(payloadStart + 2, 2));
                sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(payloadStart + 4, 4));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(payloadStart + 14, 2));
            }
            else if (chunkId.SequenceEqual("data"u8))
            {
                data = bytes.AsSpan(payloadStart, chunkSize);
            }

            offset = payloadStart + chunkSize + (chunkSize % 2);
        }

        if (channels <= 0 || sampleRate <= 0)
        {
            throw new InvalidDataException("WAV fmt chunk is missing");
        }

        if (bitsPerSample != 16)
        {
            throw new InvalidDataException($"unsupported bit depth {bitsPerSample}; expected 16-bit PCM");
        }

        if (data.IsEmpty)
        {
            throw new InvalidDataException("WAV data chunk is empty");
        }

        var frameCount = data.Length / (channels * sizeof(short));
        var mono = new float[frameCount];
        for (var frame = 0; frame < frameCount; frame++)
        {
            var sum = 0f;
            for (var channel = 0; channel < channels; channel++)
            {
                var index = (frame * channels + channel) * sizeof(short);
                var sample = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(index, sizeof(short)));
                sum += sample / 32768f;
            }

            mono[frame] = sum / channels;
        }

        return new PcmAudio(Resample(mono, sampleRate, TargetSampleRateHz), TargetSampleRateHz);
    }

    private static float[] Resample(float[] samples, int sourceRate, int targetRate)
    {
        if (sourceRate == targetRate || samples.Length == 0)
        {
            return samples;
        }

        var ratio = (double)targetRate / sourceRate;
        var targetLength = Math.Max(1, (int)Math.Round(samples.Length * ratio));
        var output = new float[targetLength];
        for (var i = 0; i < targetLength; i++)
        {
            var position = i / ratio;
            var left = (int)Math.Floor(position);
            var right = Math.Min(samples.Length - 1, left + 1);
            var weight = (float)(position - left);
            output[i] = samples[Math.Min(left, samples.Length - 1)] * (1 - weight) + samples[right] * weight;
        }

        return output;
    }
}
