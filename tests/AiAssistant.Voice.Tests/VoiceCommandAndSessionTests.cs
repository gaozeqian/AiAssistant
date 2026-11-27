using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AiAssistant.Application.Ports;
using AiAssistant.Application.UseCases;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Testing.Common;
using AiAssistant.Voice;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiAssistant.Voice.Tests;

[Trait("Category", "Unit")]
public sealed class VoiceCommandAndSessionTests
{
    [Fact]
    public void Parser_MapsFixedCommands_AndMarksExitAsSensitive()
    {
        Assert.Equal(VoiceCommandKind.GenerateReport, VoiceCommandParser.Parse("请生成今天的日报").Kind);
        Assert.Equal(VoiceCommandKind.PauseCapture, VoiceCommandParser.Parse("暂停采集").Kind);
        Assert.Equal(VoiceCommandKind.ResumeCapture, VoiceCommandParser.Parse("恢复记录").Kind);
        Assert.Equal(VoiceCommandKind.OpenTimeline, VoiceCommandParser.Parse("打开时间线").Kind);
        Assert.Equal(VoiceCommandKind.OpenReports, VoiceCommandParser.Parse("查看日报").Kind);
        Assert.True(VoiceCommandParser.Parse("退出应用").RequiresConfirmation);
        Assert.Equal(VoiceCommandKind.Confirm, VoiceCommandParser.Parse("确认").Kind);
        Assert.Equal(VoiceCommandKind.Cancel, VoiceCommandParser.Parse("取消").Kind);
        Assert.Equal(VoiceCommandKind.Unknown, VoiceCommandParser.Parse("今天天气怎么样").Kind);
    }

    [Fact]
    public async Task Router_ReusesCaptureAndReportUseCases_AndNavigationHub()
    {
        var coordinator = new FakeCoordinator();
        var reportQueue = new FakeReportQueue();
        var navigator = new VoiceNavigationHub();
        var navigated = false;
        navigator.ReportsRequested += (_, _) => navigated = true;
        var router = new VoiceCommandRouter(
            new ReportUseCase(reportQueue, new FakeReportRepository(), new FakeClock()),
            new CaptureControlUseCase(coordinator, new FakeAutoStart()),
            navigator,
            new FakeClock());

        _ = await router.ExecuteAsync(VoiceCommandParser.Parse("暂停采集"), CancellationToken.None);
        Assert.True(coordinator.IsPaused);

        _ = await router.ExecuteAsync(VoiceCommandParser.Parse("生成日报"), CancellationToken.None);
        Assert.Single(reportQueue.Enqueued);

        _ = await router.ExecuteAsync(VoiceCommandParser.Parse("打开日报"), CancellationToken.None);
        Assert.True(navigated);
    }

    [Fact]
    public void Parser_OpensFreeFormQuestionOnlyAfterFixedTrigger()
    {
        var asked = VoiceCommandParser.Parse("问一下昨天做了什么");
        Assert.Equal(VoiceCommandKind.AskQuestion, asked.Kind);
        Assert.Equal("昨天做了什么", asked.Question);

        var searched = VoiceCommandParser.Parse("搜索迁移记录");
        Assert.Equal(VoiceCommandKind.AskQuestion, searched.Kind);
        Assert.Equal("迁移记录", searched.Question);

        Assert.Equal(VoiceCommandKind.Unknown, VoiceCommandParser.Parse("问").Kind);
        Assert.Equal(VoiceCommandKind.Unknown, VoiceCommandParser.Parse("今天天气怎么样").Kind);
        Assert.Equal(VoiceCommandKind.PauseCapture, VoiceCommandParser.Parse("暂停采集").Kind);
    }

    [Fact]
    public async Task Router_AnswersQuestionWithSharedEvidenceUseCase()
    {
        var evidence = new EvidenceReference(
            "E1",
            "e1",
            new DateOnly(2026, 11, 20),
            new DateTimeOffset(2026, 11, 20, 1, 0, 0, TimeSpan.Zero),
            "Rider",
            "迁移方案",
            "结构化证据摘录",
            false,
            "keyword",
            0.4);
        var answer = new EvidenceQaAnswer(
            "迁移进展",
            "已完成迁移方案评审 [E1]。",
            [evidence],
            ["E1"],
            "fake-chat",
            ModelEndpointTier.LocalOffline,
            false,
            null);
        var router = new VoiceCommandRouter(
            new ReportUseCase(new FakeReportQueue(), new FakeReportRepository(), new FakeClock()),
            new CaptureControlUseCase(new FakeCoordinator(), new FakeAutoStart()),
            new VoiceNavigationHub(),
            new FakeClock(),
            new EvidenceSearchUseCase(new FakeEvidenceSearch(evidence), new FakeAnswerer(answer), new FakeClock()));

        var outcome = await router.ExecuteAsync(
            VoiceCommandParser.Parse("问一下迁移进展"),
            CancellationToken.None);

        Assert.True(outcome.Handled);
        Assert.Contains("已完成迁移方案评审", outcome.Feedback, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Session_WakeListenProcessRespondSleep_ExecutesCommandAndLogsTextOnly()
    {
        var harness = new Harness();
        harness.Asr.Responses.Enqueue("暂停采集");
        await harness.Session.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => harness.Hub.SubscriberCount == 1);

        harness.Hub.Publish(DetectionFrame(harness.Clock));
        await WaitUntilAsync(() => harness.Session.State == VoiceSessionState.Listening);
        harness.Hub.Publish(CommandFrame(harness.Clock));
        harness.Clock.Advance(TimeSpan.FromSeconds(9));
        harness.Hub.Publish(CommandFrame(harness.Clock));

        await WaitUntilAsync(() => harness.Log.Records.Count == 1 && harness.Session.State == VoiceSessionState.Sleeping,
            message: () => $"state={harness.Session.State}, logs={harness.Log.Records.Count}, paused={harness.Coordinator.IsPaused}, asrQueue={harness.Asr.Responses.Count}");
        Assert.True(harness.Coordinator.IsPaused);
        Assert.Equal(VoiceCommandKind.PauseCapture.ToString(), harness.Log.Records[0].Intent);
        Assert.Null(harness.Log.Records[0].AudioContentId);
        Assert.Empty(harness.Media.Writes);
        Assert.True(harness.Playback.PlayCount >= 1);
        Assert.Contains(VoiceSessionState.Listening, harness.States);
        Assert.Contains(VoiceSessionState.Processing, harness.States);
        Assert.Contains(VoiceSessionState.Responding, harness.States);

        await harness.Session.StopAsync(CancellationToken.None);
        await WaitUntilAsync(() => harness.Hub.SubscriberCount == 0);
    }

    [Fact]
    public async Task Session_Capability_DistinguishesDisabledFromArmed()
    {
        var harness = new Harness();

        var disabled = harness.Session.Capability;
        Assert.Equal(CapabilityStatus.Supported, disabled.Status);
        Assert.Contains("未启用", disabled.Summary, StringComparison.Ordinal);
        Assert.NotNull(disabled.RemediationHint);

        await harness.Session.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => harness.Hub.SubscriberCount == 1);

        var armed = harness.Session.Capability;
        Assert.Equal(CapabilityStatus.Supported, armed.Status);
        Assert.Contains("已启用", armed.Summary, StringComparison.Ordinal);
        Assert.Null(armed.RemediationHint);

        await harness.Session.StopAsync(CancellationToken.None);
        Assert.Contains("未启用", harness.Session.Capability.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Session_HandlesTwoConsecutiveCommandsWithoutRestart()
    {
        var harness = new Harness();
        harness.Asr.Responses.Enqueue("暂停采集");
        harness.Asr.Responses.Enqueue("恢复采集");
        await harness.Session.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => harness.Hub.SubscriberCount == 1);

        // Round 1: wake → listen → process → respond → sleep.
        harness.Hub.Publish(DetectionFrame(harness.Clock));
        await WaitUntilAsync(() => harness.Session.State == VoiceSessionState.Listening);
        harness.Hub.Publish(CommandFrame(harness.Clock));
        harness.Clock.Advance(TimeSpan.FromSeconds(9));
        harness.Hub.Publish(CommandFrame(harness.Clock));
        await WaitUntilAsync(
            () => harness.Log.Records.Count == 1 && harness.Session.State == VoiceSessionState.Sleeping,
            message: () => $"first round: state={harness.Session.State}, logs={harness.Log.Records.Count}");
        Assert.True(harness.Coordinator.IsPaused);

        // Round 2: the false-wake cooldown is two seconds, so move past it before waking again.
        harness.Clock.Advance(TimeSpan.FromSeconds(3));
        harness.Hub.Publish(DetectionFrame(harness.Clock));
        await WaitUntilAsync(
            () => harness.Session.State == VoiceSessionState.Listening,
            message: () => $"second wake: state={harness.Session.State}, active={harness.Session.IsActive}");
        harness.Hub.Publish(CommandFrame(harness.Clock));
        harness.Clock.Advance(TimeSpan.FromSeconds(9));
        harness.Hub.Publish(CommandFrame(harness.Clock));

        await WaitUntilAsync(
            () => harness.Log.Records.Count == 2 && harness.Session.State == VoiceSessionState.Sleeping,
            message: () => $"second round: state={harness.Session.State}, logs={harness.Log.Records.Count}, asrQueue={harness.Asr.Responses.Count}");

        Assert.False(harness.Coordinator.IsPaused);
        Assert.True(harness.Session.IsActive);
        Assert.Equal(2, harness.Log.Records.Count);

        await harness.Session.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Session_CommandAudioOptIn_PersistsEncryptedWavAndLinksLog()
    {
        var harness = new Harness(recordCommandAudio: true);
        harness.Asr.Responses.Enqueue("暂停采集");
        await harness.Session.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => harness.Hub.SubscriberCount == 1);

        harness.Hub.Publish(DetectionFrame(harness.Clock));
        await WaitUntilAsync(() => harness.Session.State == VoiceSessionState.Listening);
        harness.Hub.Publish(CommandFrame(harness.Clock));
        harness.Clock.Advance(TimeSpan.FromSeconds(9));
        harness.Hub.Publish(CommandFrame(harness.Clock));

        await WaitUntilAsync(
            () => harness.Log.Records.Count == 1 && harness.Session.State == VoiceSessionState.Sleeping,
            message: () => $"state={harness.Session.State}, logs={harness.Log.Records.Count}, writes={harness.Media.Writes.Count}");

        var write = Assert.Single(harness.Media.Writes);
        Assert.Equal(ContentKind.AudioPcm16Wav, write.Kind);
        Assert.Equal(write.CreatedAtUtc.Add(TimeSpan.FromDays(7)), write.ExpiresAtUtc);
        Assert.Equal(harness.Log.Records[0].AudioContentId, write.ContentId);

        var wav = write.Plaintext.Span;
        Assert.True(wav.Length > 44);
        Assert.Equal("RIFF"u8.ToArray(), wav[..4].ToArray());
        Assert.Equal("WAVE"u8.ToArray(), wav[8..12].ToArray());
        Assert.Equal(16_000, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(wav[24..28]));

        await harness.Session.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Session_CommandAudioOptIn_WithoutMediaStore_IsRejected()
    {
        var harness = new Harness(withMediaStore: false);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Session.SetCommandAudioRecordingAsync(true, CancellationToken.None));
        Assert.False(harness.Session.RecordsCommandAudio);
    }

    [Fact]
    public async Task Session_SensitiveCommand_RequiresSecondConfirmation()
    {
        var harness = new Harness();
        harness.Asr.Responses.Enqueue("退出应用");
        harness.Asr.Responses.Enqueue("确认");
        await harness.Session.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => harness.Hub.SubscriberCount == 1);

        harness.Hub.Publish(DetectionFrame(harness.Clock));
        await WaitUntilAsync(() => harness.Session.State == VoiceSessionState.Listening);
        harness.Hub.Publish(CommandFrame(harness.Clock));
        harness.Clock.Advance(TimeSpan.FromSeconds(9));
        harness.Hub.Publish(CommandFrame(harness.Clock));
        await WaitUntilAsync(() => harness.Session.State == VoiceSessionState.Listening && harness.Log.Records.Count == 1,
            message: () => $"state={harness.Session.State}, logs={harness.Log.Records.Count}, asrQueue={harness.Asr.Responses.Count}");
        Assert.Equal(0, harness.Navigator.ExitCount);

        harness.Hub.Publish(CommandFrame(harness.Clock));
        harness.Clock.Advance(TimeSpan.FromSeconds(9));
        harness.Hub.Publish(CommandFrame(harness.Clock));
        await WaitUntilAsync(() => harness.Navigator.ExitCount == 1 && harness.Log.Records.Count == 2);
        Assert.Equal(VoiceCommandKind.ExitApplication.ToString(), harness.Log.Records[1].Intent);

        await harness.Session.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Session_MissingWakeModel_DoesNotOpenMicrophone()
    {
        var options = new SherpaVoiceOptions { ModelDirectory = TempArtifactDirectory.CreatePath("voice") };
        using var detector = new SherpaOnnxWakeWordDetector(options, NullLogger<SherpaOnnxWakeWordDetector>.Instance);
        // Missing assets are a degraded capability, not a permission the user can grant in the OS.
        Assert.Equal(CapabilityStatus.Degraded, detector.Capability.Status);
        Assert.NotNull(detector.Capability.RemediationHint);
        Assert.Empty(detector.Accept(new AudioFrame(DateTimeOffset.UtcNow, 16_000, 1, new float[512])));
    }

    [Fact]
    public async Task Router_QuestionTimeout_FallsBackToEvidenceDigest()
    {
        var evidence = new EvidenceReference(
            "E1",
            "e1",
            new DateOnly(2026, 11, 20),
            new DateTimeOffset(2026, 11, 20, 1, 0, 0, TimeSpan.Zero),
            "Rider",
            "迁移方案",
            "结构化证据摘录",
            false,
            "keyword",
            0.4);
        var router = new VoiceCommandRouter(
            new ReportUseCase(new FakeReportQueue(), new FakeReportRepository(), new FakeClock()),
            new CaptureControlUseCase(new FakeCoordinator(), new FakeAutoStart()),
            new VoiceNavigationHub(),
            new FakeClock(),
            new EvidenceSearchUseCase(
                new FakeEvidenceSearch(evidence),
                new FakeAnswerer(new EvidenceQaAnswer(
                    "迁移进展",
                    "不应返回的模型答案 [E1]",
                    [evidence],
                    ["E1"],
                    "fake-chat",
                    ModelEndpointTier.LocalOffline,
                    false,
                    null))
                {
                    HangForever = true
                },
                new FakeClock()))
        {
            QuestionTimeout = TimeSpan.FromMilliseconds(100)
        };

        var outcome = await router.ExecuteAsync(
            VoiceCommandParser.Parse("问一下迁移进展"),
            CancellationToken.None);

        Assert.True(outcome.Handled);
        Assert.Contains("模型响应超时", outcome.Feedback, StringComparison.Ordinal);
        Assert.Contains("迁移方案", outcome.Feedback, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Session_WedgedFeedbackPlayback_StillReturnsToSleepingAndHearsNextWake()
    {
        var harness = new Harness();
        harness.Asr.Responses.Enqueue("暂停采集");
        harness.Asr.Responses.Enqueue("恢复采集");
        await harness.Session.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => harness.Hub.SubscriberCount == 1);

        harness.Playback.HangForever = true;
        harness.Hub.Publish(DetectionFrame(harness.Clock));
        await WaitUntilAsync(() => harness.Session.State == VoiceSessionState.Listening);
        harness.Hub.Publish(CommandFrame(harness.Clock));
        harness.Clock.Advance(TimeSpan.FromSeconds(9));
        harness.Hub.Publish(CommandFrame(harness.Clock));

        // The command itself succeeded; only the spoken feedback is wedged. The state machine must
        // still converge to Sleeping instead of stranding the session in Responding forever.
        await WaitUntilAsync(
            () => harness.Session.State == VoiceSessionState.Sleeping,
            timeoutMs: 5000,
            message: () => $"state={harness.Session.State}, logs={harness.Log.Records.Count}");

        // Once back in Sleeping, the next wake word must be heard again.
        harness.Playback.HangForever = false;
        harness.Clock.Advance(TimeSpan.FromSeconds(3));
        harness.Hub.Publish(DetectionFrame(harness.Clock));
        await WaitUntilAsync(
            () => harness.Session.State == VoiceSessionState.Listening,
            timeoutMs: 3000,
            message: () => $"second wake: state={harness.Session.State}, active={harness.Session.IsActive}");

        await harness.Session.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Session_FeedbackPlaybackHonoursCancellation()
    {
        var harness = new Harness();
        harness.Asr.Responses.Enqueue("暂停采集");
        await harness.Session.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => harness.Hub.SubscriberCount == 1);

        harness.Hub.Publish(DetectionFrame(harness.Clock));
        await WaitUntilAsync(() => harness.Session.State == VoiceSessionState.Listening);
        harness.Hub.Publish(CommandFrame(harness.Clock));
        harness.Clock.Advance(TimeSpan.FromSeconds(9));
        harness.Hub.Publish(CommandFrame(harness.Clock));
        await WaitUntilAsync(() => harness.Session.State == VoiceSessionState.Sleeping);

        var stop = harness.Session.StopAsync(CancellationToken.None);
        await stop.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Session_StopAbandonsHandlerStuckInNativeInference()
    {
        var harness = new Harness();
        harness.Asr.Responses.Enqueue("暂停采集");
        await harness.Session.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => harness.Hub.SubscriberCount == 1);

        harness.Asr.HangForever = true;
        harness.Hub.Publish(DetectionFrame(harness.Clock));
        await WaitUntilAsync(() => harness.Session.State == VoiceSessionState.Listening);
        harness.Hub.Publish(CommandFrame(harness.Clock));
        harness.Clock.Advance(TimeSpan.FromSeconds(9));
        harness.Hub.Publish(CommandFrame(harness.Clock));
        await WaitUntilAsync(() => harness.Session.State == VoiceSessionState.Processing);

        // ASR ignores cancellation; StopAsync must still give the UI back instead of hanging.
        await harness.Session.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(harness.Session.IsActive);
        Assert.Equal(VoiceSessionState.Sleeping, harness.Session.State);
    }

    private static AudioFrame DetectionFrame(FakeClock clock) =>
        new(clock.UtcNow, 16_000, 1, new[] { 1f, 0f, 0f, 0f });

    private static AudioFrame CommandFrame(FakeClock clock) =>
        new(clock.UtcNow, 16_000, 1, Enumerable.Repeat(0.4f, 1600).ToArray());

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000, Func<string>? message = null)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), message?.Invoke());
    }

    private sealed class Harness
    {
        public FakeClock Clock { get; } = new();
        public FakeAudioHub Hub { get; } = new();
        public FakeWakeWordDetector Kws { get; } = new();
        public FakeAsr Asr { get; } = new();
        public FakeTts Tts { get; } = new();
        public FakePlayback Playback { get; } = new();
        public FakeCoordinator Coordinator { get; } = new();
        public FakeCommandLog Log { get; } = new();
        public FakeMediaStore Media { get; } = new();
        public CountingNavigator Navigator { get; } = new();
        public List<VoiceSessionState> States { get; } = [];
        public VoiceSessionOptions Options { get; }
        public VoiceSessionController Session { get; }

        public Harness(bool recordCommandAudio = false, bool withMediaStore = true)
        {
            Options = new VoiceSessionOptions
            {
                CommandWindow = TimeSpan.FromSeconds(8),
                FeedbackTimeout = TimeSpan.FromSeconds(1),
                StopTimeout = TimeSpan.FromSeconds(1),
                RecordCommandAudio = recordCommandAudio
            };
            var router = new VoiceCommandRouter(
                new ReportUseCase(new FakeReportQueue(), new FakeReportRepository(), Clock),
                new CaptureControlUseCase(Coordinator, new FakeAutoStart()),
                Navigator,
                Clock);
            Session = new VoiceSessionController(
                Hub,
                Kws,
                Asr,
                Tts,
                Playback,
                router,
                Log,
                Clock,
                Options,
                NullLogger<VoiceSessionController>.Instance,
                withMediaStore ? Media : null);
            Session.StateChanged += (_, state) => States.Add(state);
        }
    }

    private sealed class FakeEvidenceSearch(EvidenceReference evidence) : IEvidenceSearch
    {
        public Task<SearchQueryResult> SearchAsync(
            string query,
            DateOnly fromDate,
            DateOnly toDate,
            CancellationToken ct) =>
            Task.FromResult(new SearchQueryResult(query, fromDate, toDate, [evidence], 1, 0));
    }

    private sealed class FakeAnswerer(EvidenceQaAnswer answer) : IEvidenceQuestionAnswerer
    {
        /// <summary>Simulates a large local model that outlives the voice Q&amp;A budget.</summary>
        public bool HangForever { get; init; }

        public async Task<EvidenceQaAnswer> AnswerAsync(
            string question,
            DateOnly fromDate,
            DateOnly toDate,
            CancellationToken ct)
        {
            if (HangForever)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }

            return answer;
        }
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
        public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public void Advance(TimeSpan delta) => UtcNow = UtcNow.Add(delta);
    }

    private sealed class FakeAudioHub : IAudioHub
    {
        private readonly Channel<AudioFrame> _channel = Channel.CreateUnbounded<AudioFrame>();
        private int _subscriberCount;
        public int SubscriberCount => Volatile.Read(ref _subscriberCount);
        public CapabilityState Capability => CapabilityState.Supported(CapabilityKind.AudioCapture, "fake");
        public int ActiveSubscriberCount => SubscriberCount;

        public void Publish(AudioFrame frame) => _channel.Writer.TryWrite(frame);

        public async IAsyncEnumerable<AudioFrame> SubscribeAsync(
            AudioSubscription subscription,
            [EnumeratorCancellation] CancellationToken ct)
        {
            Interlocked.Increment(ref _subscriberCount);
            try
            {
                await foreach (var frame in _channel.Reader.ReadAllAsync(ct))
                {
                    yield return frame;
                }
            }
            finally
            {
                Interlocked.Decrement(ref _subscriberCount);
            }
        }

        public ValueTask DisposeAsync()
        {
            _channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeWakeWordDetector : IWakeWordDetector
    {
        public CapabilityState Capability => CapabilityState.Supported(CapabilityKind.WakeWord, "fake KWS");
        public IReadOnlyList<WakeWordDetection> Accept(AudioFrame frame) =>
            frame.Samples.Span[0] == 1f
                ? [new WakeWordDetection("你好时光", frame.TimestampUtc)]
                : [];
        public void Reset() { }
        public void Dispose() { }
    }

    private sealed class FakeAsr : IAsrEngine
    {
        public Queue<string> Responses { get; } = new();

        /// <summary>Simulates native inference that never observes the cancellation token.</summary>
        public bool HangForever { get; set; }

        public CapabilityState Capability => CapabilityState.Supported(CapabilityKind.AudioCapture, "fake ASR");
        public Task<AudioTranscriptionResult> TranscribeAsync(AudioTranscriptionRequest request, CancellationToken ct) =>
            HangForever
                ? new TaskCompletionSource<AudioTranscriptionResult>().Task
                : Task.FromResult(new AudioTranscriptionResult(
                    Responses.Count > 0 ? Responses.Dequeue() : string.Empty,
                    0.95,
                    "fake-asr"));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeTts : ITtsService
    {
        public CapabilityState Capability => CapabilityState.Supported(CapabilityKind.WakeWord, "fake TTS");
        public Task<SpeechSynthesisResult> SynthesizeAsync(SpeechSynthesisRequest request, CancellationToken ct) =>
            Task.FromResult(new SpeechSynthesisResult(new float[1600], 16_000, "fake-tts"));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakePlayback : IAudioPlayback
    {
        public int PlayCount { get; private set; }

        /// <summary>Simulates a wedged output device: the task never completes and ignores cancellation.</summary>
        public bool HangForever { get; set; }

        public CapabilityState Capability => CapabilityState.Supported(CapabilityKind.WakeWord, "fake playback");
        public Task PlayAsync(ReadOnlyMemory<float> samples, int sampleRateHz, CancellationToken ct)
        {
            PlayCount++;
            return HangForever
                ? new TaskCompletionSource().Task
                : Task.CompletedTask;
        }
    }

    private sealed class FakeCoordinator : ICaptureCoordinator
    {
        public bool IsPaused { get; private set; }
        public IReadOnlyList<string> BlacklistedPatterns => [];
        public event EventHandler<CapturePipelineMetrics>? MetricsUpdated;
        public CapturePipelineMetrics GetMetricsSnapshot() => new(IsPaused, 0, 0, 0, 0, 0, 0);
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task PauseAsync(string reason, CancellationToken ct)
        {
            IsPaused = true;
            MetricsUpdated?.Invoke(this, GetMetricsSnapshot());
            return Task.CompletedTask;
        }

        public Task ResumeAsync(CancellationToken ct)
        {
            IsPaused = false;
            MetricsUpdated?.Invoke(this, GetMetricsSnapshot());
            return Task.CompletedTask;
        }

        public Task SetBlacklistedPatternsAsync(IReadOnlyList<string> patterns, CancellationToken ct) => Task.CompletedTask;
        public Task SubmitSampleAsync(CapturedSample sample, CancellationToken ct) => Task.CompletedTask;
        public Task CaptureImmediateSnapshotAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeAutoStart : IAutoStartService
    {
        public CapabilityState Capability => CapabilityState.Supported(CapabilityKind.AutoStart, "fake");
        public Task<bool> IsEnabledAsync(CancellationToken ct) => Task.FromResult(false);
        public Task SetEnabledAsync(bool enabled, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeReportQueue : IReportJobQueue
    {
        public List<ReportJobRequest> Enqueued { get; } = [];
        public Task<EnqueueResult> EnqueueAsync(ReportJobRequest request, CancellationToken ct)
        {
            Enqueued.Add(request);
            return Task.FromResult(new EnqueueResult($"job_{Enqueued.Count}", false));
        }
        public Task<int> RecoverInterruptedJobsAsync(CancellationToken ct) => Task.FromResult(0);
        public Task<ReportJob?> ClaimNextPendingAsync(CancellationToken ct) => Task.FromResult<ReportJob?>(null);
        public Task UpdateAsync(ReportJob job, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<ReportJob>> ListRecentJobsAsync(int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ReportJob>>([]);
    }

    private sealed class FakeReportRepository : IReportRepository
    {
        public Task<DailyReport?> GetLatestAsync(DateOnly localDate, CancellationToken ct) => Task.FromResult<DailyReport?>(null);
        public Task<IReadOnlyList<DailyReport>> ListVersionsAsync(DateOnly localDate, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DailyReport>>([]);
        public Task<DailyReport> AppendAsync(
            DateOnly localDate, string markdown, string statsJson, string modelId,
            ModelEndpointTier endpointTier, bool redactionApplied, ReportTriggerSource triggerSource,
            DateTimeOffset generatedAtUtc, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeCommandLog : IVoiceCommandLog
    {
        public List<VoiceCommandLogRecord> Records { get; } = [];

        public Task AppendAsync(VoiceCommandLogRecord record, CancellationToken ct)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<VoiceCommandLogRecord>> ListRecentAsync(int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<VoiceCommandLogRecord>>(
                Records.OrderByDescending(record => record.OccurredAtUtc).Take(limit).ToArray());
    }

    private sealed class FakeMediaStore : IMediaStore
    {
        public List<ContentWriteRequest> Writes { get; } = [];

        public Task<ContentRef> PutAsync(ContentWriteRequest request, CancellationToken ct)
        {
            Writes.Add(request);
            return Task.FromResult(new ContentRef(
                request.ContentId,
                request.Kind,
                "sha256-test",
                request.Plaintext.Length,
                request.CreatedAtUtc,
                request.ExpiresAtUtc));
        }

        public Task<byte[]?> ReadDecryptedAsync(string contentId, CancellationToken ct) =>
            Task.FromResult<byte[]?>(null);

        public Task<int> IncrementRefAsync(string contentId, CancellationToken ct) => Task.FromResult(1);

        public Task<int> DecrementRefAsync(string contentId, CancellationToken ct) => Task.FromResult(0);

        public Task<int> PurgeExpiredAsync(DateTimeOffset nowUtc, CancellationToken ct) => Task.FromResult(0);

        public Task<int> PurgeOverQuotaAsync(long maxBytes, CancellationToken ct) => Task.FromResult(0);
    }

    private sealed class CountingNavigator : IVoiceViewNavigator
    {
        public int ExitCount { get; private set; }
        public void ShowTimeline() { }
        public void ShowReports() { }
        public void RequestExit() => ExitCount++;
    }
}
