using System.Threading.Channels;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Processing;
using AiAssistant.Testing.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiAssistant.Processing.Tests;

[Trait("Category", "Unit")]
public sealed class ProcessingPipelineTests
{
    private static readonly DateTimeOffset BaseUtc = new(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Pipeline_IsolatesFailureAndTimeout_ButStillRedactsAndReturnsRawEvent()
    {
        var extractor = new FakeTextExtractor("登录 password=Sup3rSecret! 联系人 test@example.com 手机 13800138000");
        var pipeline = CreatePipeline(
            new TextExtractionStage(extractor),
            new DelegateStage("fail", TimeSpan.FromMilliseconds(200), (_, _) => throw new InvalidOperationException("boom")),
            new DelegateStage("slow", TimeSpan.FromMilliseconds(20), async (_, ct) =>
            {
                await Task.Delay(500, ct);
                return null;
            }),
            new RedactionStage());
        var raw = CreateEvent("raw-1", "SecretApp", "Private Window");

        var enriched = await pipeline.EnrichAsync(new EventProcessingRequest(raw, NativeWindowHandle: 1), CancellationToken.None);

        Assert.Equal(raw.Id, enriched.Id);
        Assert.Equal(SourceKind.WindowActivity, enriched.Source);
        Assert.NotNull(enriched.ExtractedText);
        Assert.Contains("password=[REDACTED]", enriched.ExtractedText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Sup3rSecret", enriched.ExtractedText, StringComparison.Ordinal);
        Assert.DoesNotContain("test@example.com", enriched.ExtractedText, StringComparison.Ordinal);
        Assert.DoesNotContain("13800138000", enriched.ExtractedText, StringComparison.Ordinal);
        Assert.Equal("2", enriched.Metadata["processing.failures"]);
        Assert.Contains("fail:failed", enriched.Metadata["processing.outcomes"], StringComparison.Ordinal);
        Assert.Contains("slow:failed", enriched.Metadata["processing.outcomes"], StringComparison.Ordinal);
        Assert.Contains("redaction:ok", enriched.Metadata["processing.outcomes"], StringComparison.Ordinal);

        var metrics = pipeline.GetMetrics();
        Assert.Equal(1, metrics.Runs);
        Assert.Equal(1, metrics.StageTimeouts);
        Assert.Equal(2, metrics.StageFailures);
    }

    [Fact]
    public async Task DeduplicationStage_SkipsExpensiveExtractionForNearDuplicateFrame()
    {
        var extractor = new FakeTextExtractor("first frame text");
        var pipeline = CreatePipeline(
            new PerceptualDeduplicationStage(TimeSpan.FromSeconds(10)),
            new TextExtractionStage(extractor));
        var first = CreateEvent("frame-1", "Rider", "Main.cs") with { PerceptualHash = 0b1010_0000UL };
        var second = CreateEvent("frame-2", "Rider", "Main.cs") with { PerceptualHash = 0b1010_0001UL };

        var firstResult = await pipeline.EnrichAsync(new EventProcessingRequest(first, NativeWindowHandle: 1), CancellationToken.None);
        var secondResult = await pipeline.EnrichAsync(new EventProcessingRequest(second, NativeWindowHandle: 1), CancellationToken.None);

        Assert.Equal(1, extractor.CallCount);
        Assert.Equal("first frame text", firstResult.ExtractedText);
        Assert.Null(secondResult.ExtractedText);
    }

    [Fact]
    public async Task AudioStages_AddTranscriptAndSpeakerMetadata_WithoutModelAssets()
    {
        var asr = new FakeAsrEngine("讨论架构设计与发布计划", 0.92, "fake-asr-v1");
        var diarization = new FakeDiarizationEngine(
        [
            new SpeakerTurn("speaker_0", TimeSpan.Zero, TimeSpan.FromSeconds(1)),
            new SpeakerTurn("speaker_1", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2))
        ]);
        var pipeline = CreatePipeline(
            new AudioTranscriptionStage(asr),
            new SpeakerDiarizationStage(diarization),
            new RedactionStage());
        var audio = new AudioFrame(BaseUtc, 16_000, 1, new float[16_000]);
        var raw = CreateEvent("audio-1", "TencentMeeting", "Architecture Sync", SourceKind.MicrophoneAudio);

        var enriched = await pipeline.EnrichAsync(
            new EventProcessingRequest(raw, Audio: audio),
            CancellationToken.None);

        Assert.Equal("讨论架构设计与发布计划", enriched.ExtractedText);
        Assert.Equal("fake-asr-v1", enriched.Metadata["processing.asr_model"]);
        Assert.Equal("0.920", enriched.Metadata["processing.asr_confidence"]);
        Assert.Equal("2", enriched.Metadata["processing.speakers"]);
        Assert.Equal("fake-diarization-v1", enriched.Metadata["processing.diarization_model"]);
    }

    [Fact]
    public void Redactor_RemovesCredentialsAndIdentifiers_ButPreservesNormalChineseText()
    {
        const string privateKey = "-----BEGIN RSA PRIVATE KEY-----\nMIIEsecret\n-----END RSA PRIVATE KEY-----";
        var input = $"今天完成了架构评审。api_key=abcd1234 Bearer abcdefghijklmnopqrstuvwxyz {privateKey} 联系 13912345678";
        var redactor = new SensitiveDataRedactor();

        var result = redactor.Redact(input);

        Assert.Contains("今天完成了架构评审", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("abcd1234", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("MIIEsecret", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("13912345678", result.Text, StringComparison.Ordinal);
        Assert.True(result.RedactionCount >= 4);
    }

    [Fact]
    public async Task SherpaEngines_WithoutModelAssets_DegradeWithoutLoadingNativeModels()
    {
        var options = new SherpaOnnxOptions
        {
            ModelDirectory = TempArtifactDirectory.CreatePath("processing")
        };
        await using var asr = new SherpaOnnxAsrEngine(options, NullLogger<SherpaOnnxAsrEngine>.Instance);
        using var vad = new SherpaOnnxVadSegmenter(options, NullLogger<SherpaOnnxVadSegmenter>.Instance);
        await using var diarization = new SherpaOnnxDiarizationEngine(options, NullLogger<SherpaOnnxDiarizationEngine>.Instance);

        Assert.Equal(CapabilityStatus.PermissionRequired, asr.Capability.Status);
        Assert.Equal(CapabilityStatus.PermissionRequired, vad.Capability.Status);
        Assert.Equal(CapabilityStatus.PermissionRequired, diarization.Capability.Status);

        var transcription = await asr.TranscribeAsync(
            new AudioTranscriptionRequest(new float[1600], 16_000),
            CancellationToken.None);
        Assert.Empty(transcription.Text);
        Assert.Empty(vad.Accept(new AudioFrame(BaseUtc, 16_000, 1, new float[1024])));
        Assert.Empty(await diarization.DiarizeAsync(new float[16_000], 16_000, CancellationToken.None));
    }

    private static ProcessingPipeline CreatePipeline(params IProcessingStage<ProcessingEnvelope, ProcessingEnvelope>[] stages) =>
        new(stages, NullLogger<ProcessingPipeline>.Instance);

    private static ActivityEvent CreateEvent(
        string id,
        string app,
        string title,
        SourceKind source = SourceKind.WindowActivity) =>
        new(
            id,
            OccurredTimestamp.FromInstant(BaseUtc, TimeZoneInfo.Utc),
            source,
            app,
            title,
            extractedText: null);

    private sealed class FakeTextExtractor(string text) : ITextExtractor
    {
        public int CallCount { get; private set; }
        public CapabilityState AccessibilityCapability => CapabilityState.Supported(CapabilityKind.AccessibilityText, "fake");
        public CapabilityState OcrCapability => CapabilityState.Supported(CapabilityKind.Ocr, "fake");

        public Task<ExtractedTextResult> ExtractAsync(TextExtractionRequest request, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(new ExtractedTextResult(text, TextExtractionMethod.AccessibilityTree, 0.95));
        }
    }

    private sealed class DelegateStage(
        string name,
        TimeSpan timeout,
        Func<ProcessingEnvelope, CancellationToken, Task<ProcessingEnvelope?>> action)
        : IProcessingStage<ProcessingEnvelope, ProcessingEnvelope>
    {
        public string StageName => name;
        public string StageVersion => "test";
        public TimeSpan DefaultTimeout => timeout;
        public IReadOnlyCollection<string> ResourceClaims => ["cpu"];

        public async Task<ProcessingEnvelope> ProcessAsync(ProcessingEnvelope input, CancellationToken ct) =>
            await action(input, ct) ?? input;
    }

    private sealed class FakeAsrEngine(string text, double confidence, string model) : IAsrEngine
    {
        public CapabilityState Capability => CapabilityState.Supported(CapabilityKind.AudioCapture, "fake ASR");

        public Task<AudioTranscriptionResult> TranscribeAsync(AudioTranscriptionRequest request, CancellationToken ct) =>
            Task.FromResult(new AudioTranscriptionResult(text, confidence, model));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeDiarizationEngine(IReadOnlyList<SpeakerTurn> turns) : ISpeakerDiarizationEngine
    {
        public CapabilityState Capability => CapabilityState.Supported(CapabilityKind.AudioCapture, "fake diarization");
        public string ModelVersion => "fake-diarization-v1";

        public Task<IReadOnlyList<SpeakerTurn>> DiarizeAsync(ReadOnlyMemory<float> samples, int sampleRateHz, CancellationToken ct) =>
            Task.FromResult(turns);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

[Trait("Category", "Unit")]
public sealed class AudioHubTests
{
    private static readonly DateTimeOffset BaseUtc = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AudioHub_OpensOneCaptureForManySubscribers_AndReleasesAfterLastLeaves()
    {
        var factory = new FakeCaptureFactory();
        await using var hub = new AudioHub(factory, NullLogger<AudioHub>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var kws = hub.SubscribeAsync(new AudioSubscription("kws", IncludePreRollRingBuffer: true), cts.Token)
            .GetAsyncEnumerator(cts.Token);
        var firstMove = kws.MoveNextAsync().AsTask();
        await WaitUntilAsync(() => hub.ActiveSubscriberCount == 1);

        var firstFrame = Frame(1);
        factory.Current!.Publish(firstFrame);
        Assert.True(await firstMove.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, factory.CreateCount);

        var vad = hub.SubscribeAsync(new AudioSubscription("vad", IncludePreRollRingBuffer: true), cts.Token)
            .GetAsyncEnumerator(cts.Token);
        var preRollMove = vad.MoveNextAsync().AsTask();
        Assert.True(await preRollMove.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1f, vad.Current.Samples.Span[0]);
        Assert.Equal(2, hub.ActiveSubscriberCount);

        var kwsNext = kws.MoveNextAsync().AsTask();
        var vadNext = vad.MoveNextAsync().AsTask();
        factory.Current.Publish(Frame(2));
        Assert.True(await kwsNext.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(await vadNext.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(2f, kws.Current.Samples.Span[0]);
        Assert.Equal(2f, vad.Current.Samples.Span[0]);
        Assert.Equal(1, factory.CreateCount);

        await kws.DisposeAsync();
        await WaitUntilAsync(() => hub.ActiveSubscriberCount == 1);
        Assert.False(factory.Current.Disposed);

        await vad.DisposeAsync();
        await WaitUntilAsync(() => hub.ActiveSubscriberCount == 0);
        Assert.True(factory.Current.Disposed);

        var reopened = hub.SubscribeAsync(new AudioSubscription("asr"), cts.Token).GetAsyncEnumerator(cts.Token);
        var reopenedMove = reopened.MoveNextAsync().AsTask();
        await WaitUntilAsync(() => hub.ActiveSubscriberCount == 1);
        Assert.Equal(2, factory.CreateCount);
        factory.Current!.Publish(Frame(3));
        Assert.True(await reopenedMove.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(3f, reopened.Current.Samples.Span[0]);
        await reopened.DisposeAsync();
    }

    [Fact]
    public async Task AudioHub_CancellationAndDisposeReleaseSubscribersWithoutPersistingAudio()
    {
        var factory = new FakeCaptureFactory();
        var hub = new AudioHub(factory, NullLogger<AudioHub>.Instance);
        using var cts = new CancellationTokenSource();
        var subscription = hub.SubscribeAsync(new AudioSubscription("kws"), cts.Token).GetAsyncEnumerator(cts.Token);
        var move = subscription.MoveNextAsync().AsTask();
        await WaitUntilAsync(() => hub.ActiveSubscriberCount == 1);

        factory.Current!.Publish(Frame(7));
        Assert.True(await move.WaitAsync(TimeSpan.FromSeconds(2)));
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await subscription.MoveNextAsync());
        await subscription.DisposeAsync();
        await WaitUntilAsync(() => hub.ActiveSubscriberCount == 0);

        await hub.DisposeAsync();
        Assert.True(factory.Current.Disposed);
    }

    private static AudioFrame Frame(float value) => new(BaseUtc, 16_000, 1, new[] { value, value });

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private sealed class FakeCaptureFactory : IAudioCaptureFactory
    {
        public int CreateCount { get; private set; }
        public FakeAudioCapture? Current { get; private set; }
        public CapabilityState Capability => CapabilityState.Supported(CapabilityKind.AudioCapture, "fake capture");

        public IAudioCapture Create()
        {
            CreateCount++;
            Current = new FakeAudioCapture();
            return Current;
        }
    }

    private sealed class FakeAudioCapture : IAudioCapture
    {
        private readonly Channel<AudioFrame> _channel = Channel.CreateUnbounded<AudioFrame>();
        public bool Disposed { get; private set; }
        public CapabilityState Capability => CapabilityState.Supported(CapabilityKind.AudioCapture, "fake capture");

        public void Publish(AudioFrame frame) => _channel.Writer.TryWrite(frame);

        public async IAsyncEnumerable<AudioFrame> ReadFramesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var frame in _channel.Reader.ReadAllAsync(ct))
            {
                yield return frame;
            }
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
