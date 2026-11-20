using AiAssistant.Application.Ports;
using AiAssistant.Core.ValueObjects;
using Microsoft.Extensions.Logging;
using SherpaOnnx;

namespace AiAssistant.Processing;

/// <summary>
/// Local model assets are external to Git. Paths are resolved once and never logged in full.
/// </summary>
public sealed class SherpaOnnxOptions
{
    public string ModelDirectory { get; init; } = string.Empty;
    public string AsrModelFileName { get; init; } = "model.int8.onnx";
    public string TokensFileName { get; init; } = "tokens.txt";
    public string VadModelFileName { get; init; } = "silero_vad.onnx";
    public string DiarizationSegmentationFileName { get; init; } = "segmentation.onnx";
    public string SpeakerEmbeddingFileName { get; init; } = "speaker-embedding.onnx";
    public string AsrModelVersion { get; init; } = "sherpa-onnx-paraformer-zh";
    public int Threads { get; init; } = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2));

    public string AsrModelPath => Resolve(AsrModelFileName);
    public string TokensPath => Resolve(TokensFileName);
    public string VadModelPath => Resolve(VadModelFileName);
    public string SegmentationModelPath => Resolve(DiarizationSegmentationFileName);
    public string SpeakerEmbeddingPath => Resolve(SpeakerEmbeddingFileName);

    public bool HasAsrAssets => File.Exists(AsrModelPath) && File.Exists(TokensPath);
    public bool HasVadAssets => File.Exists(VadModelPath);
    public bool HasDiarizationAssets => File.Exists(SegmentationModelPath) && File.Exists(SpeakerEmbeddingPath);

    private string Resolve(string fileName) =>
        string.IsNullOrWhiteSpace(ModelDirectory) ? string.Empty : Path.Combine(ModelDirectory, fileName);
}

public sealed class SherpaOnnxAsrEngine : IAsrEngine
{
    private readonly SherpaOnnxOptions _options;
    private readonly ILogger<SherpaOnnxAsrEngine> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OfflineRecognizer? _recognizer;
    private bool _disposed;

    public SherpaOnnxAsrEngine(SherpaOnnxOptions options, ILogger<SherpaOnnxAsrEngine> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    public CapabilityState Capability => _options.HasAsrAssets
        ? CapabilityState.Supported(CapabilityKind.AudioCapture, $"sherpa-onnx Paraformer Chinese ASR ({_options.AsrModelVersion})")
        : CapabilityState.PermissionRequired(
            CapabilityKind.AudioCapture,
            "Chinese ASR model assets are not installed",
            "Set AIASSISTANT_SHERPA_MODEL_DIR to a directory containing model.int8.onnx and tokens.txt.");

    public async Task<AudioTranscriptionResult> TranscribeAsync(AudioTranscriptionRequest request, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_options.HasAsrAssets || request.SampleRateHz != 16_000 || request.Samples.Length == 0)
        {
            return AudioTranscriptionResult.Empty;
        }

        await _gate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            var recognizer = GetRecognizer();
            using var stream = recognizer.CreateStream();
            stream.AcceptWaveform(16_000, request.Samples.ToArray());
            recognizer.Decode(stream);
            var text = stream.Result.Text?.Trim() ?? string.Empty;
            return new AudioTranscriptionResult(
                text,
                string.IsNullOrEmpty(text) ? 0 : 0.90,
                _options.AsrModelVersion);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("sherpa-onnx ASR degraded with {ExceptionType}.", ex.GetType().Name);
            return AudioTranscriptionResult.Empty;
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_recognizer is IDisposable disposable)
            {
                disposable.Dispose();
            }

            _recognizer = null;
            _gate.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private OfflineRecognizer GetRecognizer()
    {
        if (_recognizer is not null)
        {
            return _recognizer;
        }

        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = 16_000;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Paraformer.Model = _options.AsrModelPath;
        config.ModelConfig.Tokens = _options.TokensPath;
        config.ModelConfig.NumThreads = _options.Threads;
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = "greedy_search";
        _recognizer = new OfflineRecognizer(config);
        return _recognizer;
    }
}

/// <summary>Silero VAD segmentation fed from the shared AudioHub stream.</summary>
public sealed class SherpaOnnxVadSegmenter : IVadSegmenter
{
    private readonly SherpaOnnxOptions _options;
    private readonly ILogger<SherpaOnnxVadSegmenter> _logger;
    private readonly Lock _sync = new();
    private readonly List<float> _pending = [];
    private VoiceActivityDetector? _vad;
    private bool _disposed;

    public SherpaOnnxVadSegmenter(SherpaOnnxOptions options, ILogger<SherpaOnnxVadSegmenter> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    public CapabilityState Capability => _options.HasVadAssets
        ? CapabilityState.Supported(CapabilityKind.WakeWord, "sherpa-onnx Silero VAD")
        : CapabilityState.PermissionRequired(
            CapabilityKind.WakeWord,
            "Silero VAD model asset is not installed",
            "Set AIASSISTANT_SHERPA_MODEL_DIR to a directory containing silero_vad.onnx.");

    public IReadOnlyList<VadSegment> Accept(AudioFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_options.HasVadAssets || frame.SampleRateHz != 16_000)
        {
            return [];
        }

        lock (_sync)
        {
            foreach (var sample in frame.Samples.Span)
            {
                _pending.Add(sample);
            }

            const int windowSize = 512;
            var vad = GetVad();
            var segments = new List<VadSegment>();
            while (_pending.Count >= windowSize)
            {
                var window = _pending.Take(windowSize).ToArray();
                _pending.RemoveRange(0, windowSize);
                vad.AcceptWaveform(window);
                Drain(vad, segments);
            }

            return segments;
        }
    }

    public IReadOnlyList<VadSegment> Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            if (_vad is null)
            {
                return [];
            }

            _vad.Flush();
            var segments = new List<VadSegment>();
            Drain(_vad, segments);
            _pending.Clear();
            return segments;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_sync)
        {
            if (_vad is IDisposable disposable)
            {
                disposable.Dispose();
            }

            _vad = null;
            _pending.Clear();
        }
    }

    private VoiceActivityDetector GetVad()
    {
        if (_vad is not null)
        {
            return _vad;
        }

        var config = new VadModelConfig();
        config.SileroVad.Model = _options.VadModelPath;
        config.SileroVad.Threshold = 0.3F;
        config.SileroVad.MinSilenceDuration = 0.5F;
        config.SileroVad.MinSpeechDuration = 0.25F;
        config.SileroVad.MaxSpeechDuration = 15.0F;
        config.SileroVad.WindowSize = 512;
        config.Debug = 0;
        _vad = new VoiceActivityDetector(config, 60);
        return _vad;
    }

    private static void Drain(VoiceActivityDetector vad, List<VadSegment> output)
    {
        while (!vad.IsEmpty())
        {
            var segment = vad.Front();
            var samples = segment.Samples.ToArray();
            output.Add(new VadSegment(
                TimeSpan.FromSeconds(segment.Start / 16_000.0),
                TimeSpan.FromSeconds((segment.Start + samples.Length) / 16_000.0),
                samples));
            vad.Pop();
        }
    }
}

public sealed class SherpaOnnxDiarizationEngine : ISpeakerDiarizationEngine
{
    private readonly SherpaOnnxOptions _options;
    private readonly ILogger<SherpaOnnxDiarizationEngine> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OfflineSpeakerDiarization? _engine;

    public SherpaOnnxDiarizationEngine(SherpaOnnxOptions options, ILogger<SherpaOnnxDiarizationEngine> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    public CapabilityState Capability => _options.HasDiarizationAssets
        ? CapabilityState.Supported(CapabilityKind.AudioCapture, "sherpa-onnx pyannote segmentation + speaker embedding")
        : CapabilityState.PermissionRequired(
            CapabilityKind.AudioCapture,
            "Speaker diarization model assets are not installed",
            "Set AIASSISTANT_SHERPA_MODEL_DIR with segmentation.onnx and speaker-embedding.onnx.");

    public string ModelVersion => "sherpa-onnx-pyannote-3.0";

    public async Task<IReadOnlyList<SpeakerTurn>> DiarizeAsync(
        ReadOnlyMemory<float> samples,
        int sampleRateHz,
        CancellationToken ct)
    {
        if (!_options.HasDiarizationAssets || sampleRateHz != 16_000 || samples.Length == 0)
        {
            return [];
        }

        await _gate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            var engine = GetEngine();
            var result = await Task.Run(() => engine.Process(samples.ToArray()), ct);
            return result
                .Select(segment => new SpeakerTurn(
                    $"speaker_{segment.Speaker}",
                    TimeSpan.FromSeconds(segment.Start),
                    TimeSpan.FromSeconds(segment.End)))
                .ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("sherpa-onnx diarization degraded with {ExceptionType}.", ex.GetType().Name);
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    private OfflineSpeakerDiarization GetEngine()
    {
        if (_engine is not null)
        {
            return _engine;
        }

        var config = new OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = _options.SegmentationModelPath;
        config.Segmentation.Pyannote.WindowShiftRatio = 0.1F;
        config.Embedding.Model = _options.SpeakerEmbeddingPath;
        config.Clustering.Threshold = 0.5F;
        _engine = new OfflineSpeakerDiarization(config);
        return _engine;
    }

    public ValueTask DisposeAsync()
    {
        if (_engine is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _engine = null;
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
