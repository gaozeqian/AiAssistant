using System.Runtime.CompilerServices;
using AiAssistant.Application.Ports;
using AiAssistant.Core.ValueObjects;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AiAssistant.Platform.Windows;

/// <summary>Creates a WASAPI shared-mode capture only while AudioHub has active subscribers.</summary>
public sealed class WindowsAudioCaptureFactory(IClock clock) : IAudioCaptureFactory
{
    private volatile string? _lastErrorType;

    public CapabilityState Capability => !OperatingSystem.IsWindows()
        ? CapabilityState.Unavailable(CapabilityKind.AudioCapture, "WASAPI 在当前主机不可用")
        : _lastErrorType is null
            ? CapabilityState.Supported(
                CapabilityKind.AudioCapture,
                "WASAPI 共享模式已接入（麦克风待启用语音后验证）")
            : CapabilityState.Unavailable(
                CapabilityKind.AudioCapture,
                $"WASAPI 采集不可用（{_lastErrorType}）",
                "请检查麦克风设备与系统隐私设置，然后在「语音」页重新启用。");

    public IAudioCapture Create() => new WindowsWasapiAudioCapture(
        clock,
        errorType => _lastErrorType = errorType);
}

/// <summary>
/// WASAPI microphone capture normalized to 16 kHz mono float32 for the shared AudioHub.
/// Device buffers are copied before leaving the callback so subscribers never observe reused memory.
/// </summary>
public sealed class WindowsWasapiAudioCapture(
    IClock clock,
    Action<string?>? reportError = null) : IAudioCapture
{
    private volatile string? _lastErrorType;

    public CapabilityState Capability => !OperatingSystem.IsWindows()
        ? CapabilityState.Unavailable(CapabilityKind.AudioCapture, "WASAPI 在当前主机不可用")
        : _lastErrorType is null
            ? CapabilityState.Supported(CapabilityKind.AudioCapture, "WASAPI 麦克风采集")
            : CapabilityState.Unavailable(
                CapabilityKind.AudioCapture,
                $"WASAPI 麦克风采集异常（{_lastErrorType}）",
                "请检查麦克风设备与系统隐私设置，然后在「语音」页重新启用。");

    public async IAsyncEnumerable<AudioFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        WasapiRecorder recorder;
        try
        {
            recorder = new WasapiRecorderBuilder()
                .WithSharedMode()
                .WithFormat(new WaveFormat(16_000, 16, 1))
                .WithBufferLength(100)
                .Build();
        }
        catch (Exception ex)
        {
            ReportError(ex.GetType().Name);
            yield break;
        }

        // A try block containing yield return cannot also catch, so the enumerator is driven
        // explicitly: failures are recorded for the capability row and then surface to AudioHub.
        var enumerator = recorder.CaptureAsync(ct).GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                bool hasFrame;
                try
                {
                    hasFrame = await enumerator.MoveNextAsync();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    ReportError(ex.GetType().Name);
                    throw;
                }

                if (!hasFrame)
                {
                    break;
                }

                var data = enumerator.Current.Data.ToArray();
                var samples = NormalizeTo16KhzMono(data, data.Length, recorder.WaveFormat);
                if (samples.Length == 0)
                {
                    continue;
                }

                ReportSuccess();
                yield return new AudioFrame(clock.UtcNow, 16_000, 1, samples);
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
            await recorder.DisposeAsync();
        }
    }

    private void ReportError(string errorType)
    {
        _lastErrorType = errorType;
        reportError?.Invoke(errorType);
    }

    private void ReportSuccess()
    {
        if (_lastErrorType is null)
        {
            return;
        }

        _lastErrorType = null;
        reportError?.Invoke(null);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public static float[] NormalizeTo16KhzMono(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        if (format.Channels <= 0 || format.SampleRate <= 0)
        {
            return [];
        }

        var channels = format.Channels;
        var frames = format.Encoding == WaveFormatEncoding.IeeeFloat
            ? bytesRecorded / (4 * channels)
            : bytesRecorded / (2 * channels);
        if (frames <= 0)
        {
            return [];
        }

        var mono = new float[frames];
        for (var frame = 0; frame < frames; frame++)
        {
            var sum = 0f;
            for (var channel = 0; channel < channels; channel++)
            {
                var index = frame * channels + channel;
                sum += format.Encoding == WaveFormatEncoding.IeeeFloat
                    ? BitConverter.ToSingle(buffer, index * 4)
                    : BitConverter.ToInt16(buffer, index * 2) / 32768f;
            }

            mono[frame] = sum / channels;
        }

        if (format.SampleRate == 16_000)
        {
            return mono;
        }

        var ratio = (double)format.SampleRate / 16_000;
        var outputLength = Math.Max(1, (int)(frames / ratio));
        var resampled = new float[outputLength];
        for (var i = 0; i < outputLength; i++)
        {
            var position = i * ratio;
            var left = Math.Min(frames - 1, (int)position);
            var right = Math.Min(frames - 1, left + 1);
            var fraction = position - left;
            resampled[i] = (float)(mono[left] * (1 - fraction) + mono[right] * fraction);
        }

        return resampled;
    }
}
