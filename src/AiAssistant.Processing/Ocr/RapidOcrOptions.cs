namespace AiAssistant.Processing.Ocr;

/// <summary>
/// RapidOCR (PP-OCRv4) model layout. Assets stay outside Git; a partial set degrades OCR
/// without affecting AT-SPI text or capture.
/// </summary>
public sealed class RapidOcrOptions
{
    public string ModelDirectory { get; init; } = string.Empty;
    public string DetModelFileName { get; init; } = "ch_PP-OCRv4_det_infer.onnx";
    public string RecModelFileName { get; init; } = "ch_PP-OCRv4_rec_infer.onnx";
    public string KeysFileName { get; init; } = "ppocr_keys_v1.txt";

    /// <summary>Per-pixel probability above which a pixel is treated as text.</summary>
    public float PixelThreshold { get; init; } = 0.3f;

    /// <summary>Minimum mean probability for a connected component to become a text box.</summary>
    public float BoxThreshold { get; init; } = 0.5f;

    /// <summary>Box padding factor approximating DB unclip, applied around each component bounding box.</summary>
    public float UnclipRatio { get; init; } = 1.6f;

    public int MaxSideLength { get; init; } = 960;
    public int RecHeight { get; init; } = 48;
    public int RecMaxWidth { get; init; } = 640;
    public int MaxTextLength { get; init; } = 2048;
    public int Threads { get; init; } = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2));
    public string ModelVersion { get; init; } = "rapidocr-ppocrv4";

    public string DetModelPath => Resolve(DetModelFileName);
    public string RecModelPath => Resolve(RecModelFileName);
    public string KeysPath => Resolve(KeysFileName);

    public bool HasAssets =>
        File.Exists(DetModelPath) &&
        File.Exists(RecModelPath) &&
        File.Exists(KeysPath);

    private string Resolve(string fileName) =>
        string.IsNullOrWhiteSpace(ModelDirectory) ? string.Empty : Path.Combine(ModelDirectory, fileName);
}

/// <summary>Axis-aligned text box in original image coordinates.</summary>
public readonly record struct RapidOcrBox(int X, int Y, int Width, int Height, double Score);
