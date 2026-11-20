using AiAssistant.Application.Ports;
using AiAssistant.Core.ValueObjects;
using NAudio.Wave;

namespace AiAssistant.Platform.Windows;

/// <summary>Short-lived WASAPI/WinMM playback for TTS feedback only.</summary>
public sealed class WindowsAudioPlayback : IAudioPlayback
{
    public CapabilityState Capability => OperatingSystem.IsWindows()
        ? CapabilityState.Supported(CapabilityKind.WakeWord, "WinMM WaveOut feedback")
        : CapabilityState.Unavailable(CapabilityKind.WakeWord, "Windows playback inactive on this host");

    public async Task PlayAsync(ReadOnlyMemory<float> samples, int sampleRateHz, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows() || samples.Length == 0 || sampleRateHz <= 0)
        {
            return;
        }

        var pcm = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var value = (short)Math.Clamp(samples.Span[i] * 32767f, short.MinValue, short.MaxValue);
            pcm[i * 2] = (byte)(value & 0xFF);
            pcm[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }

        var format = new WaveFormat(sampleRateHz, 16, 1);
        var provider = new BufferedWaveProvider(
            format,
            TimeSpan.FromSeconds(Math.Max(1, samples.Length / (double)sampleRateHz + 1)))
        {
            DiscardOnBufferOverflow = true
        };
        using var output = new WaveOut();
        output.Init(provider);
        output.Play();
        provider.AddSamples(pcm, 0, pcm.Length);

        while (!ct.IsCancellationRequested)
        {
            if (provider.BufferedBytes == 0 && output.PlaybackState != PlaybackState.Playing)
            {
                break;
            }

            await Task.Delay(30, ct);
        }

        output.Stop();
    }
}
