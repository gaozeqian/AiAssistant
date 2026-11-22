using System.Runtime.InteropServices;
using AiAssistant.Application.Ports;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Platform.Linux;

/// <summary>PulseAudio/PipeWire-compatible short feedback playback via libpulse-simple.</summary>
public sealed class LinuxAudioPlayback : IAudioPlayback
{
    public CapabilityState Capability => OperatingSystem.IsLinux()
        ? CapabilityState.Supported(CapabilityKind.WakeWord, "PulseAudio 提示音播放")
        : CapabilityState.Unavailable(CapabilityKind.WakeWord, "PulseAudio 播放在当前主机不可用");

    public Task PlayAsync(ReadOnlyMemory<float> samples, int sampleRateHz, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux() || samples.Length == 0 || sampleRateHz <= 0)
        {
            return Task.CompletedTask;
        }

        var pcm = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var value = (short)Math.Clamp(samples.Span[i] * 32767f, short.MinValue, short.MaxValue);
            pcm[i * 2] = (byte)(value & 0xFF);
            pcm[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }

        try
        {
            var spec = new PulsePlaybackNative.pa_sample_spec
            {
                format = 3,
                rate = (uint)sampleRateHz,
                channels = 1
            };
            var stream = PulsePlaybackNative.pa_simple_new(
                0,
                "AiAssistant",
                1, // PLAYBACK
                0,
                "voice-feedback",
                ref spec,
                0,
                0,
                out _);
            if (stream == 0)
            {
                return Task.CompletedTask;
            }

            try
            {
                _ = PulsePlaybackNative.pa_simple_write(stream, pcm, (nuint)pcm.Length, out _);
                _ = PulsePlaybackNative.pa_simple_drain(stream, out _);
            }
            finally
            {
                PulsePlaybackNative.pa_simple_free(stream);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            // Feedback is optional; missing PulseAudio must not stop voice commands.
        }

        return Task.CompletedTask;
    }

    private static partial class PulsePlaybackNative
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct pa_sample_spec
        {
            internal int format;
            internal uint rate;
            internal byte channels;
        }

        [DllImport("libpulse-simple.so.0", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
        internal static extern nint pa_simple_new(
            nint server,
            string name,
            int direction,
            nint device,
            string streamName,
            ref pa_sample_spec sampleSpec,
            nint channelMap,
            nint bufferAttributes,
            out int error);

        [DllImport("libpulse-simple.so.0")]
        internal static extern int pa_simple_write(nint stream, byte[] data, nuint bytes, out int error);

        [DllImport("libpulse-simple.so.0")]
        internal static extern int pa_simple_drain(nint stream, out int error);

        [DllImport("libpulse-simple.so.0")]
        internal static extern void pa_simple_free(nint stream);
    }
}
