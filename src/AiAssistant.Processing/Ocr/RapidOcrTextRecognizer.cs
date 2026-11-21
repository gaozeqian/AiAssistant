using AiAssistant.Application.Ports;
using AiAssistant.Core.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AiAssistant.Processing.Ocr;

/// <summary>
/// On-demand Linux image OCR fallback for windows whose AT-SPI tree exposes no text.
/// Only used when the PP-OCRv4 det/rec models and key dictionary are installed locally.
/// </summary>
public sealed class RapidOcrTextRecognizer : IImageOcrRecognizer, IDisposable
{
    private const int MaxBoxesPerFrame = 64;

    private readonly RapidOcrOptions _options;
    private readonly ILogger<RapidOcrTextRecognizer> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private InferenceSession? _detection;
    private InferenceSession? _recognition;
    private IReadOnlyList<string>? _dictionary;
    private string? _errorType;
    private bool _disposed;

    public RapidOcrTextRecognizer(RapidOcrOptions options, ILogger<RapidOcrTextRecognizer> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    public CapabilityState Capability => _errorType is not null
        ? CapabilityState.Degraded(CapabilityKind.Ocr, $"RapidOCR degraded ({_errorType})")
        : _options.HasAssets
            ? CapabilityState.Supported(CapabilityKind.Ocr, $"RapidOCR PP-OCRv4 ({_options.ModelVersion})")
            : CapabilityState.PermissionRequired(
                CapabilityKind.Ocr,
                "RapidOCR ONNX models are not installed",
                "Set AIASSISTANT_RAPIDOCR_MODEL_DIR to a directory containing det/rec ONNX models and ppocr_keys_v1.txt.");

    /// <summary>Returns recognized text or null when OCR is unavailable or finds nothing.</summary>
    public async Task<string?> RecognizeAsync(ImageFrameBuffer frame, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_options.HasAssets || !RapidOcrImage.TryCreate(frame, out var image) || image is null)
        {
            return null;
        }

        await _gate.WaitAsync(ct);
        try
        {
            var text = await Task.Run(() => RecognizeCore(image, ct), ct);
            _errorType = null;
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _errorType = ex.GetType().Name;
            _logger.LogWarning("RapidOCR skipped an image after {ExceptionType}.", ex.GetType().Name);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _detection?.Dispose();
        _recognition?.Dispose();
        _detection = null;
        _recognition = null;
        _gate.Dispose();
    }

    private string RecognizeCore(RapidOcrImage image, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var detection = GetDetectionSession();
        var (tensor, width, height, scaleX, scaleY) = image.ToDetectionTensor(_options.MaxSideLength);
        var input = NamedOnnxValue.CreateFromTensor(
            detection.InputMetadata.Keys.First(),
            new DenseTensor<float>(tensor, [1, 3, height, width]));

        IReadOnlyList<RapidOcrBox> boxes;
        using (var results = detection.Run([input]))
        {
            var output = results[0].AsTensor<float>();
            var dimensions = output.Dimensions;
            var mapHeight = dimensions[^2];
            var mapWidth = dimensions[^1];
            boxes = RapidOcrDetPostProcessor.DetectBoxes(
                output.ToArray(),
                mapWidth,
                mapHeight,
                _options.PixelThreshold,
                _options.BoxThreshold,
                scaleX,
                scaleY,
                image.Width,
                image.Height,
                _options.UnclipRatio);
        }

        if (boxes.Count == 0)
        {
            return string.Empty;
        }

        var dictionary = GetDictionary();
        var recognition = GetRecognitionSession();
        var lines = new List<string>();
        foreach (var box in boxes.Take(MaxBoxesPerFrame))
        {
            ct.ThrowIfCancellationRequested();
            var (recognitionTensor, recognitionWidth) = image.ToRecognitionTensor(
                box,
                _options.RecHeight,
                _options.RecMaxWidth);
            var recognitionInput = NamedOnnxValue.CreateFromTensor(
                recognition.InputMetadata.Keys.First(),
                new DenseTensor<float>(recognitionTensor, [1, 3, _options.RecHeight, recognitionWidth]));

            using var recognitionResults = recognition.Run([recognitionInput]);
            var logits = recognitionResults[0].AsTensor<float>();
            var dimensions = logits.Dimensions;
            if (dimensions.Length < 3)
            {
                continue;
            }

            var (text, _) = RapidOcrCtcDecoder.GreedyDecode(
                logits.ToArray(),
                dimensions[^2],
                dimensions[^1],
                dictionary);
            if (!string.IsNullOrWhiteSpace(text))
            {
                lines.Add(text.Trim());
            }
        }

        var joined = string.Join('\n', lines);
        return joined.Length <= _options.MaxTextLength ? joined : joined[.._options.MaxTextLength];
    }

    private InferenceSession GetDetectionSession() => _detection ??= CreateSession(_options.DetModelPath);

    private InferenceSession GetRecognitionSession() => _recognition ??= CreateSession(_options.RecModelPath);

    private IReadOnlyList<string> GetDictionary()
    {
        if (_dictionary is not null)
        {
            return _dictionary;
        }

        _dictionary = File.ReadAllLines(_options.KeysPath);
        return _dictionary;
    }

    private InferenceSession CreateSession(string modelPath)
    {
        var sessionOptions = new SessionOptions
        {
            IntraOpNumThreads = _options.Threads,
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };
        return new InferenceSession(modelPath, sessionOptions);
    }
}
