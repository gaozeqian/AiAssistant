using AiAssistant.Application.Imaging;
using AiAssistant.Application.Ports;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Platform.Linux;
using AiAssistant.Platform.Linux.Accessibility;

namespace AiAssistant.Platform.Tests;

public sealed class LinuxTextExtractorOcrTests
{
    [Fact]
    public async Task TextExtractor_PrefersAccessibilityTextOverOcr()
    {
        var atSpi = new FakeAtSpiClient("ACCESSIBLE_TEXT");
        var ocr = new FakeOcr("OCR_TEXT");
        var extractor = new LinuxTextExtractor(atSpi, ocr);

        var result = await extractor.ExtractAsync(
            new TextExtractionRequest(42, "Editor", "Main.cs", WhitePng(16, 16)),
            CancellationToken.None);

        Assert.Equal("ACCESSIBLE_TEXT", result.Text);
        Assert.Equal(TextExtractionMethod.AccessibilityTree, result.Method);
        Assert.Equal(0, ocr.Calls);
    }

    [Fact]
    public async Task TextExtractor_FallsBackToOcrWhenAccessibilityIsEmpty()
    {
        var atSpi = new FakeAtSpiClient(text: null);
        var ocr = new FakeOcr("界面识别文本");
        var extractor = new LinuxTextExtractor(atSpi, ocr);

        Assert.Equal(CapabilityStatus.Supported, extractor.OcrCapability.Status);

        var result = await extractor.ExtractAsync(
            new TextExtractionRequest(0, "Rider", "Dashboard", WhitePng(16, 16)),
            CancellationToken.None);

        Assert.Equal("界面识别文本", result.Text);
        Assert.Equal(TextExtractionMethod.SystemOcr, result.Method);
        Assert.Equal(1, ocr.Calls);
        Assert.Equal(16, ocr.LastFrame?.Width);
        Assert.Equal(16, ocr.LastFrame?.Height);
    }

    [Fact]
    public async Task TextExtractor_SkipsOcrWhenNoScreenshotIsAttached()
    {
        var atSpi = new FakeAtSpiClient(text: null);
        var ocr = new FakeOcr("should-not-run");
        var extractor = new LinuxTextExtractor(atSpi, ocr);

        var result = await extractor.ExtractAsync(
            new TextExtractionRequest(0, "Rider", "Dashboard"),
            CancellationToken.None);

        Assert.Equal(TextExtractionMethod.None, result.Method);
        Assert.Equal(0, ocr.Calls);
    }

    private static byte[] WhitePng(int width, int height)
    {
        var bgra = new byte[width * height * 4];
        Array.Fill(bgra, (byte)255);
        return PngCodec.EncodeBgra32(bgra, width, height);
    }

    private sealed class FakeAtSpiClient(string? text) : IAtSpiAccessibilityClient
    {
        public CapabilityState Capability =>
            CapabilityState.Supported(CapabilityKind.AccessibilityText, "Fake AT-SPI");

        public Task<AtSpiWindowSnapshot?> GetFocusedWindowAsync(CancellationToken ct) =>
            Task.FromResult<AtSpiWindowSnapshot?>(null);

        public Task<string?> ExtractTextAsync(nint windowHandle, CancellationToken ct) =>
            Task.FromResult(text);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeOcr(string text) : IImageOcrRecognizer
    {
        public int Calls { get; private set; }

        public ImageFrameBuffer? LastFrame { get; private set; }

        public CapabilityState Capability => CapabilityState.Supported(CapabilityKind.Ocr, "Fake RapidOCR");

        public Task<string?> RecognizeAsync(ImageFrameBuffer frame, CancellationToken ct)
        {
            Calls++;
            LastFrame = frame;
            return Task.FromResult<string?>(text);
        }
    }
}
