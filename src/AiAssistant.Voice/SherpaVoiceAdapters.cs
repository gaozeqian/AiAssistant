using AiAssistant.Application.Ports;
using AiAssistant.Core.ValueObjects;
using Microsoft.Extensions.Logging;
using SherpaOnnx;

namespace AiAssistant.Voice;

public sealed class SherpaVoiceOptions
{
    public string ModelDirectory { get; init; } = string.Empty;
    public string KeywordsFile { get; init; } = "kws/keywords.txt";
    public string KwsEncoder { get; init; } = "kws/encoder.onnx";
    public string KwsDecoder { get; init; } = "kws/decoder.onnx";
    public string KwsJoiner { get; init; } = "kws/joiner.onnx";
    public string KwsTokens { get; init; } = "kws/tokens.txt";
    public string TtsModel { get; init; } = "tts/model.onnx";
    public string TtsTokens { get; init; } = "tts/tokens.txt";
    public string TtsLexicon { get; init; } = "tts/lexicon.txt";

    /// <summary>
    /// Jieba dictionary directory required by MeloTTS models. Older VITS assets do not ship one;
    /// the directory is only passed to sherpa-onnx when it exists.
    /// </summary>
    public string TtsDictDir { get; init; } = "tts/dict";

    /// <summary>
    /// Text-normalization FSTs (dates, numbers, phone numbers, heteronyms) shipped with MeloTTS.
    /// Missing entries are skipped so a stripped install still synthesizes plain text.
    /// </summary>
    public IReadOnlyList<string> TtsRuleFsts { get; init; } =
    [
        "tts/date.fst",
        "tts/number.fst",
        "tts/phone.fst",
        "tts/new_heteronym.fst"
    ];

    public int Threads { get; init; } = 1;

    /// <summary>
    /// Boosts keyword paths during decoding. Tuned on the 13-positive / 2.12 h golden set:
    /// 1.0 gave 84.6% recall, 3.0 gave 92.3% with zero false alarms, 5.0 regressed to 84.6%.
    /// </summary>
    public float KeywordsScore { get; init; } = 3.0f;

    /// <summary>Posterior threshold for accepting a keyword hit; lower values trade precision for recall.</summary>
    public float KeywordsThreshold { get; init; } = 0.25f;

    public IReadOnlyList<string> WakeWords { get; init; } = ["你好时光"];

    public string KeywordsPath => Resolve(KeywordsFile);
    public string KwsEncoderPath => Resolve(KwsEncoder);
    public string KwsDecoderPath => Resolve(KwsDecoder);
    public string KwsJoinerPath => Resolve(KwsJoiner);
    public string KwsTokensPath => Resolve(KwsTokens);
    public string TtsModelPath => Resolve(TtsModel);
    public string TtsTokensPath => Resolve(TtsTokens);
    public string TtsLexiconPath => Resolve(TtsLexicon);
    public string TtsDictDirPath => Resolve(TtsDictDir);
    public IReadOnlyList<string> TtsRuleFstsPaths => TtsRuleFsts.Select(Resolve).ToArray();

    public bool HasKwsAssets =>
        File.Exists(KeywordsPath) &&
        File.Exists(KwsEncoderPath) &&
        File.Exists(KwsDecoderPath) &&
        File.Exists(KwsJoinerPath) &&
        File.Exists(KwsTokensPath);

    public bool HasTtsAssets =>
        File.Exists(TtsModelPath) &&
        File.Exists(TtsTokensPath) &&
        File.Exists(TtsLexiconPath);

    private string Resolve(string fileName) =>
        string.IsNullOrWhiteSpace(ModelDirectory) ? string.Empty : Path.Combine(ModelDirectory, fileName);
}

public sealed class SherpaOnnxWakeWordDetector : IWakeWordDetector
{
    private readonly SherpaVoiceOptions _options;
    private readonly ILogger<SherpaOnnxWakeWordDetector> _logger;
    private readonly Lock _sync = new();
    private KeywordSpotter? _spotter;
    private OnlineStream? _stream;
    private string? _errorType;
    private bool _disposed;

    public SherpaOnnxWakeWordDetector(
        SherpaVoiceOptions options,
        ILogger<SherpaOnnxWakeWordDetector> logger)
    {
        _options = options;
        _logger = logger;
    }

    public CapabilityState Capability => _errorType is not null
        ? CapabilityState.Degraded(CapabilityKind.WakeWord, $"KWS degraded ({_errorType})")
        : _options.HasKwsAssets
            ? CapabilityState.Supported(CapabilityKind.WakeWord, "sherpa-onnx 唤醒词「你好时光」")
            : CapabilityState.PermissionRequired(
                CapabilityKind.WakeWord,
                "唤醒词模型资产未安装",
                "请在 AIASSISTANT_SHERPA_MODEL_DIR 下提供 KWS encoder/decoder/joiner/tokens 与 keywords.txt。");

    public IReadOnlyList<WakeWordDetection> Accept(AudioFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_options.HasKwsAssets || frame.SampleRateHz != 16_000 || frame.Samples.Length == 0)
        {
            return [];
        }

        lock (_sync)
        {
            try
            {
                var spotter = GetSpotter();
                var stream = _stream!;
                stream.AcceptWaveform(16_000, frame.Samples.ToArray());
                var detections = new List<WakeWordDetection>();
                while (spotter.IsReady(stream))
                {
                    spotter.Decode(stream);
                    var result = spotter.GetResult(stream);
                    if (!string.IsNullOrWhiteSpace(result.Keyword))
                    {
                        detections.Add(new WakeWordDetection(result.Keyword, frame.TimestampUtc));
                        spotter.Reset(stream);
                    }
                }

                _errorType = null;
                return detections;
            }
            catch (Exception ex)
            {
                _errorType = ex.GetType().Name;
                _logger.LogWarning("Wake-word detection degraded with {ExceptionType}.", ex.GetType().Name);
                return [];
            }
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            if (_spotter is not null && _stream is not null)
            {
                _spotter.Reset(_stream);
            }
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
            if (_stream is IDisposable streamDisposable)
            {
                streamDisposable.Dispose();
            }

            if (_spotter is IDisposable spotterDisposable)
            {
                spotterDisposable.Dispose();
            }

            _stream = null;
            _spotter = null;
        }
    }

    private KeywordSpotter GetSpotter()
    {
        if (_spotter is not null)
        {
            return _spotter;
        }

        var config = new KeywordSpotterConfig();
        config.FeatConfig.SampleRate = 16_000;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = _options.KwsEncoderPath;
        config.ModelConfig.Transducer.Decoder = _options.KwsDecoderPath;
        config.ModelConfig.Transducer.Joiner = _options.KwsJoinerPath;
        config.ModelConfig.Tokens = _options.KwsTokensPath;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.NumThreads = _options.Threads;
        config.ModelConfig.Debug = 0;
        config.KeywordsFile = _options.KeywordsPath;
        config.KeywordsScore = _options.KeywordsScore;
        config.KeywordsThreshold = _options.KeywordsThreshold;
        _spotter = new KeywordSpotter(config);
        _stream = _spotter.CreateStream();
        return _spotter;
    }
}

public sealed class SherpaOnnxTtsService : ITtsService
{
    private readonly SherpaVoiceOptions _options;
    private readonly ILogger<SherpaOnnxTtsService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OfflineTts? _tts;
    private string? _errorType;
    private bool _disposed;

    public SherpaOnnxTtsService(
        SherpaVoiceOptions options,
        ILogger<SherpaOnnxTtsService> logger)
    {
        _options = options;
        _logger = logger;
    }

    public CapabilityState Capability => _errorType is not null
        ? CapabilityState.Degraded(CapabilityKind.WakeWord, $"TTS degraded ({_errorType})")
        : _options.HasTtsAssets
            ? CapabilityState.Supported(CapabilityKind.WakeWord, "sherpa-onnx 本机语音合成")
            : CapabilityState.PermissionRequired(
                CapabilityKind.WakeWord,
                "语音合成模型资产未安装",
                "请在 AIASSISTANT_SHERPA_MODEL_DIR 下提供语音合成模型、tokens 与 lexicon。");

    public async Task<SpeechSynthesisResult> SynthesizeAsync(
        SpeechSynthesisRequest request,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_options.HasTtsAssets || string.IsNullOrWhiteSpace(request.Text))
        {
            return new SpeechSynthesisResult(ReadOnlyMemory<float>.Empty, 0, "unavailable");
        }

        await _gate.WaitAsync(ct);
        try
        {
            var text = request.Text.Length <= 200 ? request.Text : request.Text[..200];
            var result = await Task.Run(() =>
            {
                var tts = GetTts();
                var config = new OfflineTtsGenerationConfig
                {
                    Sid = 0,
                    Speed = 1.0f,
                    SilenceScale = 0.2f
                };
                var audio = tts.GenerateWithConfig(text, config, null);
                return new SpeechSynthesisResult(audio.Samples, audio.SampleRate, "sherpa-onnx-vits");
            }, ct);
            _errorType = null;
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _errorType = ex.GetType().Name;
            _logger.LogWarning("TTS synthesis degraded with {ExceptionType}.", ex.GetType().Name);
            return new SpeechSynthesisResult(ReadOnlyMemory<float>.Empty, 0, "unavailable");
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
            if (_tts is IDisposable disposable)
            {
                disposable.Dispose();
            }

            _tts = null;
            _gate.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private OfflineTts GetTts()
    {
        if (_tts is not null)
        {
            return _tts;
        }

        var config = new OfflineTtsConfig();
        config.Model.Vits.Model = _options.TtsModelPath;
        config.Model.Vits.Tokens = _options.TtsTokensPath;
        config.Model.Vits.Lexicon = _options.TtsLexiconPath;
        if (Directory.Exists(_options.TtsDictDirPath))
        {
            config.Model.Vits.DictDir = _options.TtsDictDirPath;
        }

        var ruleFsts = _options.TtsRuleFstsPaths.Where(File.Exists).ToArray();
        if (ruleFsts.Length > 0)
        {
            config.RuleFsts = string.Join(",", ruleFsts);
        }

        config.Model.Vits.NoiseScale = 0.667f;
        config.Model.Vits.NoiseScaleW = 0.8f;
        config.Model.Vits.LengthScale = 1.0f;
        config.Model.NumThreads = _options.Threads;
        config.Model.Debug = 0;
        config.Model.Provider = "cpu";
        config.MaxNumSentences = 1;
        _tts = new OfflineTts(config);
        return _tts;
    }
}
