using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Platform.macOS.Ipc;

namespace AiAssistant.Platform.macOS;

/// <summary>
/// macOS CoreAudio / AVAudioEngine 16kHz mono float32 capture adapter via Swift Helper IPC.
/// Feeds the unified S4 AudioHub so multiple consumers never contend for the microphone.
/// </summary>
public sealed class AudioCaptureFactory(IMacHelperSupervisor supervisor, IClock clock) : IAudioCaptureFactory
{
    public CapabilityState Capability => new AudioCapture(supervisor, clock).Capability;

    public IAudioCapture Create() => new AudioCapture(supervisor, clock);
}

/// <summary>macOS feedback playback is deferred to the S9 helper extension; voice text feedback remains available.</summary>
/// <summary>
/// macOS TTS feedback playback via the Swift Helper's AVAudioEngine (S7-R2).
/// Samples stay in memory as float32 PCM; no temporary audio files are written.
/// </summary>
public sealed class AudioPlayback(IMacHelperSupervisor supervisor) : IAudioPlayback
{
    private const int MaxDurationSeconds = 30;

    public CapabilityState Capability => supervisor.Status == MacHelperSupervisorStatus.Healthy
        ? CapabilityState.Supported(
            CapabilityKind.WakeWord,
            "AVAudioEngine 提示音播放（Swift Helper）")
        : CapabilityState.Degraded(
            CapabilityKind.WakeWord,
            $"macOS Helper {supervisor.Status}；语音反馈播放不可用",
            "语音命令仍可用；请在诊断页检查 Helper 状态。");

    public async Task PlayAsync(ReadOnlyMemory<float> samples, int sampleRateHz, CancellationToken ct)
    {
        if (samples.IsEmpty || sampleRateHz <= 0)
        {
            return;
        }

        // Feedback is short by design; clamp pathological payloads instead of blocking the session.
        var maxSamples = sampleRateHz * MaxDurationSeconds;
        if (samples.Length > maxSamples)
        {
            samples = samples[..maxSamples];
        }

        var pcmBytes = MemoryMarshal.AsBytes(samples.Span).ToArray();

        // Playback failures must never break a voice command; Capability already reflects helper health.
        await supervisor.InvokeAsync(
            new MacIpcRequest(
                string.Empty,
                MacHelperProtocol.Methods.PlayAudio,
                SampleRateHz: sampleRateHz,
                Float32PcmBase64: Convert.ToBase64String(pcmBytes)),
            ct);
    }
}

public sealed class AudioCapture(
    IMacHelperSupervisor supervisor,
    IClock clock,
    TimeSpan? pollInterval = null) : IAudioCapture
{
    private readonly TimeSpan _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(100);

    public CapabilityState Capability => MacHelperCapabilityEvaluator.EvaluateHelperCapability(
        supervisor,
        CapabilityKind.AudioCapture,
        supervisor.Permissions.Microphone,
        "CoreAudio / AVAudioEngine 16kHz 单声道（Swift Helper）",
        "请在「系统设置 > 隐私与安全性 > 麦克风」中启用 AiAssistant。");

    public async IAsyncEnumerable<AudioFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_pollInterval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            if (supervisor.Status is MacHelperSupervisorStatus.Unavailable or MacHelperSupervisorStatus.ProtocolIncompatible)
            {
                yield break;
            }

            var response = await supervisor.InvokeAsync(
                new MacIpcRequest(string.Empty, MacHelperProtocol.Methods.PollAudioFrames),
                ct);

            if (!response.Ok || response.AudioChunk is null || string.IsNullOrWhiteSpace(response.AudioChunk.Float32PcmBase64))
            {
                continue;
            }

            byte[] pcmBytes;
            try
            {
                pcmBytes = Convert.FromBase64String(response.AudioChunk.Float32PcmBase64);
            }
            catch (FormatException)
            {
                continue;
            }

            if (pcmBytes.Length < sizeof(float))
            {
                continue;
            }

            var sampleCount = pcmBytes.Length / sizeof(float);
            var samples = new float[sampleCount];
            MemoryMarshal.Cast<byte, float>(pcmBytes.AsSpan(0, sampleCount * sizeof(float))).CopyTo(samples);

            yield return new AudioFrame(
                clock.UtcNow,
                response.AudioChunk.SampleRateHz > 0 ? response.AudioChunk.SampleRateHz : 16_000,
                response.AudioChunk.Channels > 0 ? response.AudioChunk.Channels : 1,
                samples);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
