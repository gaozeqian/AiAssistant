using AiAssistant.Application.Ports;
using AiAssistant.Core.ValueObjects;
using NAudio;
using NAudio.Wave;

namespace AiAssistant.Platform.Windows;

/// <summary>Short-lived WASAPI/WinMM playback for TTS feedback and recorded command replays.</summary>
public sealed class WindowsAudioPlayback : IAudioPlayback
{
    /// <summary>
    /// Time granted to the output device after the clip's nominal length so the last buffers can
    /// drain before the handle is closed.
    /// </summary>
    private static readonly TimeSpan DrainTail = TimeSpan.FromMilliseconds(250);

    public CapabilityState Capability => OperatingSystem.IsWindows()
        ? CapabilityState.Supported(CapabilityKind.WakeWord, "WinMM WaveOut 提示音播放")
        : CapabilityState.Unavailable(CapabilityKind.WakeWord, "Windows 音频播放在当前主机不可用");

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
        provider.AddSamples(pcm, 0, pcm.Length);

        using var output = new WaveOut();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, _) => stopped.TrySetResult();
        try
        {
            output.Init(provider);
            output.Play();
        }
        catch (MmException ex)
        {
            // A host without a usable output device must surface as a handled failure instead of
            // faulting the caller's task with a vendor-specific exception.
            throw new InvalidOperationException("音频输出设备不可用。", ex);
        }

        // WaveOut only reports Stopped once it is told to stop; when the provider runs dry it keeps
        // reporting Playing forever. Bound the wait by the clip length instead of the device state,
        // otherwise the caller's task never completes and every replay button stays disabled.
        var duration = TimeSpan.FromSeconds(samples.Length / (double)sampleRateHz);
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await Task.WhenAny(stopped.Task, Task.Delay(duration + DrainTail, waitCts.Token));
        await waitCts.CancelAsync();
        ct.ThrowIfCancellationRequested();
        output.Stop();
    }
}
