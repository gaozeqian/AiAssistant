using System.Globalization;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;
using Microsoft.Extensions.Logging;

namespace AiAssistant.Voice;

public sealed class VoiceSessionOptions
{
    public TimeSpan CommandWindow { get; init; } = TimeSpan.FromSeconds(8);
    public TimeSpan FalseWakeCooldown { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan ConfirmationWindow { get; init; } = TimeSpan.FromSeconds(8);
    public bool EnableWakeChime { get; init; } = true;
    public int MaxCommandSamples => (int)(16_000 * CommandWindow.TotalSeconds);
}

/// <summary>
/// Explicit wake → listen → process → respond → sleep state machine.
/// KWS and command audio share the single AudioHub subscription; command audio stays in memory.
/// </summary>
public sealed class VoiceSessionController : IVoiceSession
{
    private readonly IAudioHub _audioHub;
    private readonly IWakeWordDetector _wakeWordDetector;
    private readonly IAsrEngine _asr;
    private readonly ITtsService _tts;
    private readonly IAudioPlayback _playback;
    private readonly VoiceCommandRouter _router;
    private readonly IVoiceCommandLog _commandLog;
    private readonly IClock _clock;
    private readonly VoiceSessionOptions _options;
    private readonly ILogger<VoiceSessionController> _logger;
    private readonly Lock _sync = new();
    private readonly List<float> _commandBuffer = [];
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private int _state = (int)VoiceSessionState.Sleeping;
    private DateTimeOffset _commandDeadline;
    private DateTimeOffset _lastWakeAt = DateTimeOffset.MinValue;
    private DateTimeOffset _ignoreFramesBeforeUtc = DateTimeOffset.MinValue;
    private string _wakeKeyword = "你好时光";
    private ParsedVoiceCommand? _pendingConfirmation;
    private bool _disposed;

    public VoiceSessionController(
        IAudioHub audioHub,
        IWakeWordDetector wakeWordDetector,
        IAsrEngine asr,
        ITtsService tts,
        IAudioPlayback playback,
        VoiceCommandRouter router,
        IVoiceCommandLog commandLog,
        IClock clock,
        VoiceSessionOptions options,
        ILogger<VoiceSessionController> logger)
    {
        _audioHub = audioHub;
        _wakeWordDetector = wakeWordDetector;
        _asr = asr;
        _tts = tts;
        _playback = playback;
        _router = router;
        _commandLog = commandLog;
        _clock = clock;
        _options = options;
        _logger = logger;
    }

    public VoiceSessionState State => (VoiceSessionState)Volatile.Read(ref _state);

    public CapabilityState Capability
    {
        get
        {
            if (!_wakeWordDetector.Capability.IsOperational)
            {
                return _wakeWordDetector.Capability;
            }

            if (!_asr.Capability.IsOperational)
            {
                return CapabilityState.Degraded(CapabilityKind.WakeWord, "Wake word ready; command ASR model missing");
            }

            if (!_tts.Capability.IsOperational)
            {
                return CapabilityState.Degraded(CapabilityKind.WakeWord, "Wake word ready; TTS feedback unavailable");
            }

            if (!_playback.Capability.IsOperational)
            {
                return CapabilityState.Degraded(CapabilityKind.WakeWord, "Wake word ready; audio feedback playback unavailable");
            }

            return CapabilityState.Supported(CapabilityKind.WakeWord, "Local wake word and voice commands ready");
        }
    }

    public event EventHandler<VoiceSessionState>? StateChanged;
    public event EventHandler<VoiceInteraction>? Interaction;

    public Task StartAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_runTask is not null)
            {
                return Task.CompletedTask;
            }

            if (!_wakeWordDetector.Capability.IsOperational)
            {
                SetState(VoiceSessionState.Sleeping);
                EmitInteraction(string.Empty, "唤醒词模型未安装，语音保持关闭。");
                return Task.CompletedTask;
            }

            _runCts = new CancellationTokenSource();
            _runTask = Task.Run(() => RunAsync(_runCts.Token), CancellationToken.None);
        }

        EmitInteraction(string.Empty, "语音已启用，说“你好时光”唤醒。");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_sync)
        {
            cts = _runCts;
            task = _runTask;
            _runCts = null;
            _runTask = null;
        }

        if (cts is null || task is null)
        {
            SetState(VoiceSessionState.Sleeping);
            return;
        }

        await cts.CancelAsync();
        try
        {
            await task.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Expected when the session is stopped
        }
        finally
        {
            cts.Dispose();
            _wakeWordDetector.Reset();
            SetState(VoiceSessionState.Sleeping);
            EmitInteraction(string.Empty, "语音已关闭。");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync(CancellationToken.None);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _audioHub.SubscribeAsync(new AudioSubscription("voice-session"), ct))
            {
                switch (State)
                {
                    case VoiceSessionState.Sleeping:
                        HandleSleeping(frame, ct);
                        break;
                    case VoiceSessionState.Listening:
                        await HandleListeningAsync(frame, ct);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Voice session stopped with {ExceptionType}.", ex.GetType().Name);
            SetState(VoiceSessionState.Sleeping);
            EmitInteraction(string.Empty, "语音会话已停止。");
        }
    }

    private void HandleSleeping(AudioFrame frame, CancellationToken ct)
    {
        if (frame.TimestampUtc < _ignoreFramesBeforeUtc)
        {
            return;
        }

        var detections = _wakeWordDetector.Accept(frame);
        if (detections.Count == 0)
        {
            return;
        }

        var now = _clock.UtcNow;
        if (now - _lastWakeAt < _options.FalseWakeCooldown)
        {
            return;
        }

        _lastWakeAt = now;
        _wakeKeyword = detections[0].Keyword;
        _pendingConfirmation = null;
        BeginListening(_options.CommandWindow);
        EmitInteraction(string.Empty, "我在听。");
        if (_options.EnableWakeChime)
        {
            _ = PlayFeedbackSafelyAsync("我在", ct);
        }
    }

    private async Task HandleListeningAsync(AudioFrame frame, CancellationToken ct)
    {
        if (frame.SampleRateHz == 16_000)
        {
            lock (_sync)
            {
                foreach (var sample in frame.Samples.Span)
                {
                    if (_commandBuffer.Count >= _options.MaxCommandSamples)
                    {
                        break;
                    }

                    _commandBuffer.Add(sample);
                }
            }
        }

        if (_clock.UtcNow >= _commandDeadline)
        {
            await ProcessCommandAsync(ct);
        }
    }

    private async Task ProcessCommandAsync(CancellationToken ct)
    {
        SetState(VoiceSessionState.Processing);
        float[] samples;
        lock (_sync)
        {
            samples = _commandBuffer.ToArray();
            _commandBuffer.Clear();
        }

        if (samples.Length < 1_600)
        {
            await FinishAsync("没有听清，请再说一次。", null, null, ct);
            return;
        }

        if (!_asr.Capability.IsOperational)
        {
            await FinishAsync("语音识别模型未安装。", null, null, ct);
            return;
        }

        var transcription = await _asr.TranscribeAsync(
            new AudioTranscriptionRequest(samples, 16_000),
            ct);
        var transcript = transcription.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(transcript))
        {
            await FinishAsync("没有听清，请再说一次。", string.Empty, null, ct);
            return;
        }

        var parsed = VoiceCommandParser.Parse(transcript);
        if (parsed.Kind == VoiceCommandKind.Cancel && _pendingConfirmation is not null)
        {
            _pendingConfirmation = null;
            await FinishAsync("已取消。", transcript, parsed, ct);
            return;
        }

        if (_pendingConfirmation is not null)
        {
            if (parsed.Kind == VoiceCommandKind.Confirm)
            {
                var sensitive = _pendingConfirmation;
                _pendingConfirmation = null;
                await ExecuteAndFinishAsync(sensitive, transcript, ct);
                return;
            }

            await FinishAsync("请说“确认”执行，或说“取消”。", transcript, parsed, ct);
            return;
        }

        if (parsed.RequiresConfirmation)
        {
            _pendingConfirmation = parsed;
            await LogCommandAsync(transcript, parsed);
            SetState(VoiceSessionState.Responding);
            EmitInteraction(transcript, parsed.Feedback);
            await PlayFeedbackAsync(parsed.Feedback, ct);
            BeginListening(_options.ConfirmationWindow);
            return;
        }

        await ExecuteAndFinishAsync(parsed, transcript, ct);
    }

    private async Task ExecuteAndFinishAsync(ParsedVoiceCommand command, string transcript, CancellationToken ct)
    {
        var outcome = await _router.ExecuteAsync(command, ct);
        await FinishAsync(outcome.Feedback, transcript, command, ct);
    }

    private async Task FinishAsync(
        string feedback,
        string? transcript,
        ParsedVoiceCommand? command,
        CancellationToken ct)
    {
        SetState(VoiceSessionState.Responding);
        if (!string.IsNullOrWhiteSpace(transcript))
        {
            await LogCommandAsync(transcript, command);
        }

        EmitInteraction(transcript ?? string.Empty, feedback);
        await PlayFeedbackAsync(feedback, ct);
        _pendingConfirmation = null;
        _wakeWordDetector.Reset();
        _ignoreFramesBeforeUtc = _clock.UtcNow;
        SetState(VoiceSessionState.Sleeping);
        _lastWakeAt = _clock.UtcNow;
    }

    private async Task LogCommandAsync(string transcript, ParsedVoiceCommand? command)
    {
        try
        {
            await _commandLog.AppendAsync(
                new VoiceCommandLogRecord(
                    StableId.NewEventId(_clock.UtcNow),
                    _clock.UtcNow,
                    _wakeKeyword,
                    transcript,
                    command?.Kind.ToString() ?? VoiceCommandKind.Unknown.ToString(),
                    command?.ParamsJson ?? "{}",
                    ModelEndpointTier.LocalOffline,
                    ResponseRef: null,
                    AudioContentId: null),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Voice command log failed with {ExceptionType}.", ex.GetType().Name);
        }
    }

    private async Task PlayFeedbackSafelyAsync(string text, CancellationToken ct)
    {
        try
        {
            await PlayFeedbackAsync(text, ct);
        }
        catch (OperationCanceledException)
        {
            // Session stopped while feedback was playing
        }
    }

    private async Task PlayFeedbackAsync(string text, CancellationToken ct)
    {
        try
        {
            if (!_tts.Capability.IsOperational)
            {
                return;
            }

            var speech = await _tts.SynthesizeAsync(new SpeechSynthesisRequest(text), ct);
            if (speech.Samples.Length == 0 || speech.SampleRateHz <= 0)
            {
                return;
            }

            if (_playback.Capability.IsOperational)
            {
                await _playback.PlayAsync(speech.Samples, speech.SampleRateHz, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug("Voice feedback playback skipped ({ExceptionType}).", ex.GetType().Name);
        }
    }

    private void BeginListening(TimeSpan window)
    {
        lock (_sync)
        {
            _commandBuffer.Clear();
            _commandDeadline = _clock.UtcNow + window;
        }

        SetState(VoiceSessionState.Listening);
    }

    private void SetState(VoiceSessionState state)
    {
        var previous = Interlocked.Exchange(ref _state, (int)state);
        if (previous != (int)state)
        {
            StateChanged?.Invoke(this, state);
        }
    }

    private void EmitInteraction(string transcript, string feedback) =>
        Interaction?.Invoke(this, new VoiceInteraction(State, _wakeKeyword, transcript, feedback, _clock.UtcNow));
}
