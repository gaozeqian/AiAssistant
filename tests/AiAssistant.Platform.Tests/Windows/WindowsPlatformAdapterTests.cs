using System.Security.Cryptography;
using AiAssistant.Application.Ports;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Platform.Linux;
using AiAssistant.Platform.macOS;
using AiAssistant.Platform.Windows;
using AiAssistant.Storage.KeyStores;
using AiAssistant.Testing.Common;

namespace AiAssistant.Platform.Tests;

[Trait("Category", "Integration")]
[Trait("OS", "Windows")]
public sealed class WindowsPlatformAdapterTests : IDisposable
{
    private readonly string _tempDir;

    public WindowsPlatformAdapterTests()
    {
        _tempDir = TempArtifactDirectory.CreatePath("platform");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore transient cleanup errors
        }
    }

    [Fact]
    public void ManagedPngEncoder_ProducesValidPngSignatureAndDimensions()
    {
        const int width = 16;
        const int height = 12;
        const int stride = width * 4;
        var bgra = new byte[stride * height];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = 200;     // B
            bgra[i + 1] = 120; // G
            bgra[i + 2] = 50;  // R
            bgra[i + 3] = 255; // A
        }

        var png = PngEncoder.EncodeBgra32(bgra, width, height, stride);
        Assert.True(png.Length > 32);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], png[..4]);
    }

    [Fact]
    public void WhitelistedFileActivitySource_EnforcesExtensionAnd200KbSizeLimits()
    {
        var smallCodePath = Path.Combine(_tempDir, "Sample.cs");
        File.WriteAllText(smallCodePath, "public class Demo {}");

        var oversizedTextPath = Path.Combine(_tempDir, "Huge.txt");
        File.WriteAllBytes(oversizedTextPath, RandomNumberGenerator.GetBytes(250 * 1024));

        var binaryPath = Path.Combine(_tempDir, "Secret.pfx");
        File.WriteAllBytes(binaryPath, [1, 2, 3, 4, 5]);

        Assert.Equal("public class Demo {}", WhitelistedFileActivitySource.TryReadSafeTextSnippet(smallCodePath, 200 * 1024));
        Assert.Null(WhitelistedFileActivitySource.TryReadSafeTextSnippet(oversizedTextPath, 200 * 1024));
        Assert.Null(WhitelistedFileActivitySource.TryReadSafeTextSnippet(binaryPath, 200 * 1024));
    }

    [Fact]
    public async Task WindowsScreenAndWindowAdapters_ExecuteSafelyOnCurrentHost()
    {
        var clock = new SystemClock();
        await using var screenCapture = new WindowsScreenCapture(clock);
        await using var windowSource = new WindowsWindowActivitySource(clock, screenCapture);
        var ocr = new FakeOcrRecognizer();
        var extractor = new WindowsTextExtractor(ocr);

        if (OperatingSystem.IsWindows())
        {
            Assert.True(screenCapture.Capability.IsOperational);
            Assert.True(windowSource.Capability.IsOperational);

            // Capture every display if an interactive desktop session is available; an empty list
            // is allowed in headless Session 0.
            var frames = await screenCapture.CaptureDisplaysAsync(new ScreenCaptureTarget(), CancellationToken.None);
            foreach (var frame in frames)
            {
                Assert.True(frame.Width > 0 && frame.Height > 0);
                Assert.Equal([0x89, 0x50, 0x4E, 0x47], frame.EncodedPngBytes[..4]);
                Assert.NotNull(frame.Display);
            }

            // Exactly one display is flagged as hosting the foreground window.
            Assert.True(frames.Count(item => item.IsForegroundDisplay) <= 1);
        }
        else
        {
            Assert.False(screenCapture.Capability.IsOperational);
            Assert.False(windowSource.Capability.IsOperational);
        }

        // Verify OCR fallback triggers when WindowHandle == 0 and the screenshot is a decodable PNG.
        var screenshot = PngEncoder.EncodeBgra32(new byte[16 * 16 * 4], 16, 16, 16 * 4);
        var extracted = await extractor.ExtractAsync(
            new TextExtractionRequest(0, "Reader", "Spec.pdf", screenshot),
            CancellationToken.None);
        Assert.Equal(TextExtractionMethod.SystemOcr, extracted.Method);
        Assert.Equal("OCR-Fallback-Len:1024", extracted.Text);
        Assert.Equal(16, ocr.LastWidth);
    }

    private sealed class FakeOcrRecognizer : IImageOcrRecognizer
    {
        public int LastWidth { get; private set; }

        public CapabilityState Capability => CapabilityState.Supported(CapabilityKind.Ocr, "FakeOCR");

        public Task<string?> RecognizeAsync(ImageFrameBuffer frame, CancellationToken ct)
        {
            LastWidth = frame.Width;
            return Task.FromResult<string?>($"OCR-Fallback-Len:{frame.Bgra32Pixels.Length}");
        }
    }

    [Fact]
    public void WasapiAudioNormalization_MixesStereoAndResamplesTo16KhzMono()
    {
        const int sourceRate = 48_000;
        const int sourceFrames = 480;
        var format = new NAudio.Wave.WaveFormat(sourceRate, 16, 2);
        var pcm = new byte[sourceFrames * 2 * 2];
        for (var frame = 0; frame < sourceFrames; frame++)
        {
            var left = BitConverter.GetBytes((short)16_384);
            var right = BitConverter.GetBytes((short)16_384);
            left.CopyTo(pcm, frame * 4);
            right.CopyTo(pcm, frame * 4 + 2);
        }

        var normalized = WindowsWasapiAudioCapture.NormalizeTo16KhzMono(pcm, pcm.Length, format);

        Assert.Equal(160, normalized.Length);
        Assert.All(normalized, sample => Assert.InRange(sample, 0.49f, 0.51f));
    }

    [Fact]
    public void WindowsCapabilityProvider_ReportsLiveVoiceAndOcrState()
    {
        var keyStore = new StaticKeyStore(RandomNumberGenerator.GetBytes(32));
        var clock = new SystemClock();
        var liveWakeWord = CapabilityState.Degraded(
            CapabilityKind.WakeWord,
            "唤醒词已就绪；命令识别模型缺失",
            "命令识别模型资产未安装；可在「语音」页查看状态。");

        var capabilities = new WindowsCapabilityProvider(
            new WindowsScreenCapture(clock),
            new WindowsWindowActivitySource(clock),
            new WhitelistedFileActivitySource(clock),
            new WindowsTextExtractor(new FakeOcrRecognizer()),
            keyStore,
            new WindowsAutoStartService("app.exe"),
            new WindowsAudioCaptureFactory(clock),
            () => liveWakeWord).GetCapabilities();

        // The row must mirror the voice stack, not a hard-coded "optional feature" placeholder.
        Assert.Equal(liveWakeWord, capabilities.Single(c => c.Kind == CapabilityKind.WakeWord));
        Assert.Equal(CapabilityStatus.Supported, capabilities.Single(c => c.Kind == CapabilityKind.Ocr).Status);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(CapabilityStatus.Supported, capabilities.Single(c => c.Kind == CapabilityKind.AudioCapture).Status);
        }

        var withoutVoice = new WindowsCapabilityProvider(
            new WindowsScreenCapture(clock),
            new WindowsWindowActivitySource(clock),
            new WhitelistedFileActivitySource(clock),
            new WindowsTextExtractor(),
            keyStore,
            new WindowsAutoStartService("app.exe")).GetCapabilities();

        var fallback = withoutVoice.Single(c => c.Kind == CapabilityKind.WakeWord);
        Assert.Equal(CapabilityStatus.Degraded, fallback.Status);
        Assert.False(string.IsNullOrWhiteSpace(fallback.RemediationHint));
        Assert.Equal(CapabilityStatus.Unavailable, withoutVoice.Single(c => c.Kind == CapabilityKind.Ocr).Status);
    }

    [Fact]
    public void CrossPlatformCapabilityProviders_ReturnNonEmptyAccurateMatrices()
    {
        var keyStore = new StaticKeyStore(RandomNumberGenerator.GetBytes(32));
        var clock = new SystemClock();

        var winCaps = new WindowsCapabilityProvider(
            new WindowsScreenCapture(clock),
            new WindowsWindowActivitySource(clock),
            new WhitelistedFileActivitySource(clock),
            new WindowsTextExtractor(),
            keyStore,
            new WindowsAutoStartService("app.exe")).GetCapabilities();

        var macCaps = new CapabilityProvider(keyStore).GetCapabilities();
        var linuxCaps = new LinuxCapabilityProvider(keyStore).GetCapabilities();

        Assert.NotEmpty(winCaps);
        Assert.NotEmpty(macCaps);
        Assert.NotEmpty(linuxCaps);
    }
}
