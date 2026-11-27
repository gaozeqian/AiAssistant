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

    /// <summary>
    /// Upper bound for one command (ASR + router + TTS). Without it a slow model call keeps the
    /// frame loop blocked and the wake word is not heard until it returns.
    /// </summary>
    public TimeSpan CommandExecutionTimeout { get; init; } = TimeSpan.FromSeconds(50);

    /// <summary>
    /// Upper bound for spoken feedback. The TTS engine runs native inference that cannot be
    /// interrupted by a token, so the state machine waits at most this long and then returns to
    /// sleep; otherwise a wedged synthesis or output device strands the session in Responding and
    /// the wake word is never heard again.
    /// </summary>
    public TimeSpan FeedbackTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Upper bound for stopping the pump. A frame handler stuck in native inference must not keep
    /// the UI toggle disabled forever.
    /// </summary>
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public bool EnableWakeChime { get; init; } = true;

    /// <summary>Explicit opt-in; command audio is discarded after processing unless enabled.</summary>
    public bool RecordCommandAudio { get; set; }

    /// <summary>Encrypted command audio expires with the regular media retention sweep.</summary>
    public TimeSpan CommandAudioRetention { get; set; } = TimeSpan.FromDays(7);

    public int MaxCommandSamples => (int)(16_000 * CommandWindow.TotalSeconds);
}

/// <summary>
/// Explicit wake → listen → process → respond → sleep state machine.
/// KWS and command audio share the single AudioHub subscription; command audio stays in memory
/// unless the user explicitly enables encrypted command-audio recording.
/// </summary>
public sealed class VoiceSessionController : IVoiceSession
{
    private readonly IAudioHub _audioHub;
    private readonly IWakeWordDetector _wakeWordDetector;
    private readonly IAsrEngine _asr;
    private readonly ITtsService _tts;
    private readonly IAudioPlayback _playback;
    private readonly VoiceCommandRouter _router;
    private readonly IClock _clock;
    private readonly VoiceSessionOptions _options;
    private readonly ILogger<VoiceSessionController> _logger;
    private readonly VoiceCommandAudioRecorder _commandAudio;
    private readonly VoiceFeedbackPlayer _feedback;
    private readonly VoiceCommandLogWriter _commandLogWriter;
    private readonly Lock _sync = new();
    private readonly List<float> _commandBuffer = [];
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private long _runGeneration;
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
        ILogger<VoiceSessionController> logger,
        IMediaStore? commandAudioStore = null)
    {
        _audioHub = audioHub;
        _wakeWordDetector = wakeWordDetector;
        _asr = asr;
        _tts = tts;
        _playback = playback;
        _router = router;
        _clock = clock;
        _options = options;
        _logger = logger;
        _commandAudio = new VoiceCommandAudioRecorder(commandAudioStore, options, clock, logger);
        _feedback = new VoiceFeedbackPlayer(tts, playback, logger);
        _commandLogWriter = new VoiceCommandLogWriter(commandLog, clock, logger);
    }

    public VoiceSessionState State => (VoiceSessionState)Volatile.Read(ref _state);

    public bool IsActive
    {
        get
        {
            lock (_sync)
            {
                return _runTask is not null;
            }
        }
    }

    public bool RecordsCommandAudio => _commandAudio.RecordsCommandAudio;

    public Task SetCommandAudioRecordingAsync(bool enabled, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        _commandAudio.SetEnabled(enabled);
        return Task.CompletedTask;
    }

    public CapabilityState Capability
    {
        get
        {
            if (!_audioHub.Capability.IsOperational)
            {
                var audio = _audioHub.Capability;
                // Preserve a real permission problem so the diagnostics row can offer the OS grant
                // instead of degrading into an informational hint.
                return audio.Status == CapabilityStatus.PermissionRequired
                    ? CapabilityState.PermissionRequired(CapabilityKind.WakeWord, audio.Summary, audio.RemediationHint)
                    : CapabilityState.Degraded(
                        CapabilityKind.WakeWord,
                        $"语音输入不可用：{audio.Summary}",
                        audio.RemediationHint ?? "请检查麦克风与系统隐私设置；可在「语音」页查看状态。");
            }

            // A degraded detector means the wake word itself is not usable (for example missing
            // KWS assets), so report it directly instead of claiming it is ready.
            var wakeWord = _wakeWordDetector.Capability;
            if (wakeWord.Status != CapabilityStatus.Supported)
            {
                return wakeWord;
            }

            if (!_asr.Capability.IsOperational)
            {
                return CapabilityState.Degraded(
                    CapabilityKind.WakeWord,
                    "唤醒词已就绪；命令识别模型缺失",
                    "命令识别模型资产未安装；可在「语音」页查看状态。");
            }

            if (!_tts.Capability.IsOperational)
            {
                return CapabilityState.Degraded(
                    CapabilityKind.WakeWord,
                    "唤醒词已就绪；语音反馈不可用",
                    "语音合成模型资产未安装；可在「语音」页查看状态。");
            }

            if (!_playback.Capability.IsOperational)
            {
                return CapabilityState.Degraded(
                    CapabilityKind.WakeWord,
                    "唤醒词已就绪；提示音播放不可用",
                    "提示音播放不可用；可在「语音」页查看状态。");
            }

            // Models and devices can be ready while the user has never enabled the session. Reporting
            // that as "已就绪 / 无需额外操作" hid the only step that actually turns listening on.
            if (!IsActive)
            {
                return new CapabilityState(
                    CapabilityKind.WakeWord,
                    CapabilityStatus.Supported,
                    "唤醒词与语音命令可用，语音未启用",
                    "在「语音」页启用后才会监听唤醒词。");
            }

            return CapabilityState.Supported(
                CapabilityKind.WakeWord,
                State == VoiceSessionState.Sleeping
                    ? "本机唤醒与语音命令已启用，等待唤醒词"
                    : "本机唤醒与语音命令已启用，正在响应");
        }
    }

    public event EventHandler<VoiceSessionState>? StateChanged;
    public event EventHandler<VoiceInteraction>? Interaction;
    public event EventHandler<VoiceSessionFault>? Faulted;

    public Task StartAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            // A previous run that already finished must not block re-arming: the loop clears its own
            // run state, but a completed task can still be observed here between restarts.
            if (_runTask is { IsCompleted: false })
            {
                return Task.CompletedTask;
            }

            if (_runTask is not null)
            {
                _runCts?.Dispose();
                _runCts = null;
                _runTask = null;
            }

            // Missing or broken wake-word assets must keep the microphone closed; a degraded
            // capability is not enough to arm the session.
            var wakeWord = _wakeWordDetector.Capability;
            if (wakeWord.Status != CapabilityStatus.Supported)
            {
                SetState(VoiceSessionState.Sleeping);
                EmitInteraction(string.Empty, $"{wakeWord.Summary}，语音保持关闭。");
                return Task.CompletedTask;
            }

            var runCts = new CancellationTokenSource();
            var generation = Interlocked.Increment(ref _runGeneration);
            _runCts = runCts;
            _runTask = Task.Run(() => RunAsync(runCts, generation), CancellationToken.None);
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
            // A handler stuck in native inference (ASR/TTS) never observes the token. Waiting
            // forever would keep the UI toggle disabled and leave the user unable to re-arm voice.
            await task.WaitAsync(_options.StopTimeout, ct);
        }
        catch (OperationCanceledException ex)
        {
            // Expected when the session is stopped
            AiAssistant.Application.Diagnostics.SuppressedExceptionLog.Trace("Voice.VoiceSessionController.StopAsync", ex);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning(
                "Voice session did not stop within {TimeoutSeconds} seconds; abandoning the previous loop.",
                _options.StopTimeout.TotalSeconds);
            // Retire the old loop so a restart can install a new one even if the old handler is
            // still blocked inside a native call.
            Interlocked.Increment(ref _runGeneration);
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

    private async Task RunAsync(CancellationTokenSource runCts, long generation)
    {
        var ct = runCts.Token;
        var faultReason = "音频采集已停止";
        try
        {
            await foreach (var frame in _audioHub.SubscribeAsync(new AudioSubscription("voice-session"), ct))
            {
                // A loop that was abandoned by a stop timeout must never touch the state of the
                // replacement loop, even if its blocked handler eventually returns.
                if (Volatile.Read(ref _runGeneration) != generation)
                {
                    break;
                }

                try
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
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // One bad command (router, ASR, UI marshalling) must not take the session down.
                    _logger.LogWarning("Voice frame handling failed with {ExceptionType}.", ex.GetType().Name);
                    RecoverFromFrameFault();
                }
            }
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            // Normal shutdown
            AiAssistant.Application.Diagnostics.SuppressedExceptionLog.Trace("Voice.VoiceSessionController.RunAsync", ex);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Voice session stopped with {ExceptionType}.", ex.GetType().Name);
            faultReason = "音频设备或识别引擎异常";
        }
        finally
        {
            var stoppedByUser = runCts.IsCancellationRequested;
            var superseded = Volatile.Read(ref _runGeneration) != generation;
            lock (_sync)
            {
                // Only clear our own run state: a restart may already have installed a new run.
                if (ReferenceEquals(_runCts, runCts))
                {
                    _runCts = null;
                    _runTask = null;
                }
            }

            if (!superseded)
            {
                SetState(VoiceSessionState.Sleeping);
                if (!stoppedByUser && !_disposed)
                {
                    // The microphone is closed now; the UI must stop claiming the session is armed.
                    EmitInteraction(string.Empty, "语音已停止，请重新启用。");
                    Faulted?.Invoke(this, new VoiceSessionFault(faultReason, _clock.UtcNow));
                }
            }
        }
    }

    /// <summary>Resets the state machine after a single command failed, keeping the session armed.</summary>
    private void RecoverFromFrameFault()
    {
        lock (_sync)
        {
            _commandBuffer.Clear();
        }

        _pendingConfirmation = null;
        _wakeWordDetector.Reset();
        _ignoreFramesBeforeUtc = _clock.UtcNow;
        _lastWakeAt = _clock.UtcNow;
        SetState(VoiceSessionState.Sleeping);
        EmitInteraction(string.Empty, "这条命令没有执行成功，请再说一次。");
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
            _ = _feedback.PlaySafelyAsync("我在", ct);
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

        if (_clock.UtcNow < _commandDeadline)
        {
            return;
        }

        // Bound the work so a slow ASR/model call cannot keep the frame loop blocked indefinitely;
        // the session always returns to Sleeping and the wake word becomes audible again.
        using var commandCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        commandCts.CancelAfter(_options.CommandExecutionTimeout);
        try
        {
            await ProcessCommandAsync(commandCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogInformation(
                "Voice command processing exceeded {TimeoutSeconds} seconds.",
                _options.CommandExecutionTimeout.TotalSeconds);
            await FinishAsync("命令处理超时，请再说一次。", null, null, null, ct);
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
            await FinishAsync("没有听清，请再说一次。", null, null, null, ct);
            return;
        }

        if (!_asr.Capability.IsOperational)
        {
            await FinishAsync("语音识别模型未安装。", null, null, null, ct);
            return;
        }

        var transcription = await _asr.TranscribeAsync(
            new AudioTranscriptionRequest(samples, 16_000),
            ct);
        var transcript = transcription.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(transcript))
        {
            await FinishAsync("没有听清，请再说一次。", string.Empty, null, null, ct);
            return;
        }

        // Opt-in only: the post-wake command audio is encrypted before any business action runs.
        var audioContentId = await _commandAudio.PersistAsync(samples, ct);

        var parsed = VoiceCommandParser.Parse(transcript, _clock);
        if (parsed.Kind == VoiceCommandKind.Cancel && _pendingConfirmation is not null)
        {
            _pendingConfirmation = null;
            await FinishAsync("已取消。", transcript, parsed, audioContentId, ct);
            return;
        }

        if (_pendingConfirmation is not null)
        {
            if (parsed.Kind == VoiceCommandKind.Confirm)
            {
                var sensitive = _pendingConfirmation;
                _pendingConfirmation = null;
                await ExecuteAndFinishAsync(sensitive, transcript, audioContentId, ct);
                return;
            }

            await FinishAsync("请说“确认”执行，或说“取消”。", transcript, parsed, audioContentId, ct);
            return;
        }

        if (parsed.RequiresConfirmation)
        {
            _pendingConfirmation = parsed;
            await _commandLogWriter.WriteAsync(_wakeKeyword, transcript, parsed, audioContentId, ct);
            SetState(VoiceSessionState.Responding);
            EmitInteraction(transcript, parsed.Feedback);
            await PlayFeedbackBoundedAsync(parsed.Feedback, ct);
            BeginListening(_options.ConfirmationWindow);
            return;
        }

        await ExecuteAndFinishAsync(parsed, transcript, audioContentId, ct);
    }

    private async Task ExecuteAndFinishAsync(
        ParsedVoiceCommand command,
        string transcript,
        string? audioContentId,
        CancellationToken ct)
    {
        var outcome = await _router.ExecuteAsync(command, ct);
        await FinishAsync(outcome.Feedback, transcript, command, audioContentId, ct);
    }

    private async Task FinishAsync(
        string feedback,
        string? transcript,
        ParsedVoiceCommand? command,
        string? audioContentId,
        CancellationToken ct)
    {
        SetState(VoiceSessionState.Responding);
        try
        {
            if (!string.IsNullOrWhiteSpace(transcript))
            {
                await _commandLogWriter.WriteAsync(_wakeKeyword, transcript, command, audioContentId, ct);
            }

            // Publish the text first: even when the spoken feedback is abandoned, the page still
            // shows what the command did and why it ended.
            EmitInteraction(transcript ?? string.Empty, feedback);
            await PlayFeedbackBoundedAsync(feedback, ct);
        }
        finally
        {
            // The state machine must converge no matter how the feedback path ends. Leaving it in
            // Responding silently discards every following audio frame, so the wake word would
            // never be heard again.
            _pendingConfirmation = null;
            _wakeWordDetector.Reset();
            _ignoreFramesBeforeUtc = _clock.UtcNow;
            SetState(VoiceSessionState.Sleeping);
            _lastWakeAt = _clock.UtcNow;
        }
    }

    /// <summary>
    /// Speaks one feedback line with a hard bound. Native synthesis and the output device can both
    /// stall without observing cancellation, so the command loop must not await them forever.
    /// </summary>
    private async Task PlayFeedbackBoundedAsync(string text, CancellationToken ct)
    {
        using var feedbackCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var playback = _feedback.PlayAsync(text, feedbackCts.Token);
        try
        {
            await playback.WaitAsync(_options.FeedbackTimeout, ct);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning(
                "Voice feedback exceeded {TimeoutSeconds} seconds and was abandoned.",
                _options.FeedbackTimeout.TotalSeconds);
        }
        finally
        {
            if (!playback.IsCompleted)
            {
                // Stop the abandoned clip so it cannot talk over the next command.
                await feedbackCts.CancelAsync();
                _ = playback.ContinueWith(
                    static task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
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
