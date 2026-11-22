using AiAssistant.Application.Ports;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Processing.Ocr;
using Microsoft.Extensions.Logging.Abstractions;
using AiAssistant.Core.Configuration;

namespace AiAssistant.Processing.Tests;

public sealed class RapidOcrTests
{
    [Fact]
    public async Task MissingModels_ReportPermissionRequired_AndReturnNull()
    {
        var options = new RapidOcrOptions
        {
            ModelDirectory = Path.Combine(AppContext.BaseDirectory, "test-temp", Guid.NewGuid().ToString("N"))
        };
        using var recognizer = new RapidOcrTextRecognizer(
            options,
            NullLogger<RapidOcrTextRecognizer>.Instance);

        Assert.Equal(CapabilityStatus.PermissionRequired, recognizer.Capability.Status);
        Assert.NotNull(recognizer.Capability.RemediationHint);
        Assert.Contains(EnvironmentNames.RapidOcrModelDir, recognizer.Capability.RemediationHint, StringComparison.Ordinal);

        var text = await recognizer.RecognizeAsync(WhiteFrame(32, 32), CancellationToken.None);

        Assert.Null(text);
        Assert.Equal(CapabilityStatus.PermissionRequired, recognizer.Capability.Status);
    }

    [Fact]
    public void OnnxRuntime_NativeLibraryLoadsInThisHost()
    {
        using var sessionOptions = new Microsoft.ML.OnnxRuntime.SessionOptions();

        Assert.NotNull(sessionOptions);
        Assert.NotNull(Microsoft.ML.OnnxRuntime.OrtEnv.Instance());
    }

    [Fact]
    public void DetPostProcessor_ReturnsPaddedBoxForTextRegionOnly()
    {
        const int mapWidth = 40;
        const int mapHeight = 40;
        var map = new float[mapWidth * mapHeight];
        for (var y = 10; y < 20; y++)
        {
            for (var x = 5; x < 30; x++)
            {
                map[(y * mapWidth) + x] = 0.9f;
            }
        }

        map[(30 * mapWidth) + 2] = 0.9f;

        var boxes = RapidOcrDetPostProcessor.DetectBoxes(
            map,
            mapWidth,
            mapHeight,
            pixelThreshold: 0.3f,
            boxThreshold: 0.5f,
            scaleX: 1f,
            scaleY: 1f,
            imageWidth: 100,
            imageHeight: 100,
            unclipRatio: 1.6f);

        var box = Assert.Single(boxes);
        Assert.True(box.X <= 5 && box.Y <= 10);
        Assert.True(box.X + box.Width >= 30);
        Assert.True(box.Y + box.Height >= 20);
        Assert.Equal(0.9, box.Score, precision: 3);
    }

    [Fact]
    public void DetPostProcessor_IgnoresIsolatedNoisePixels()
    {
        var map = new float[20 * 20];
        map[(5 * 20) + 7] = 0.99f;

        var boxes = RapidOcrDetPostProcessor.DetectBoxes(
            map,
            20,
            20,
            pixelThreshold: 0.3f,
            boxThreshold: 0.5f,
            scaleX: 1f,
            scaleY: 1f,
            imageWidth: 20,
            imageHeight: 20,
            unclipRatio: 1.6f);

        Assert.Empty(boxes);
    }

    [Fact]
    public void CtcDecoder_CollapsesRepeatsAndSkipsBlanks()
    {
        string[] dictionary = ["a", "b"];
        float[] logits =
        [
            1f, 0f, 0f,
            0.1f, 0.9f, 0f,
            0.1f, 0.8f, 0.1f,
            0.1f, 0.1f, 0.8f
        ];

        var (text, confidence) = RapidOcrCtcDecoder.GreedyDecode(logits, timeSteps: 4, classCount: 3, dictionary);

        Assert.Equal("ab", text);
        Assert.Equal(0.85, confidence, precision: 3);
    }

    [Fact]
    public void Image_BuildsDetectionTensorFromDecodedFrame()
    {
        var frame = WhiteFrame(64, 32);
        Assert.True(RapidOcrImage.TryCreate(frame, out var image));
        Assert.NotNull(image);
        Assert.Equal(64, image!.Width);
        Assert.Equal(32, image.Height);

        var (tensor, tensorWidth, tensorHeight, scaleX, scaleY) = image.ToDetectionTensor(maxSideLength: 960);

        Assert.Equal(64, tensorWidth);
        Assert.Equal(32, tensorHeight);
        Assert.Equal(1f, scaleX);
        Assert.Equal(1f, scaleY);
        Assert.Equal(3 * 64 * 32, tensor.Length);
        Assert.All(tensor, value => Assert.InRange(value, -3f, 3f));
    }

    [Fact]
    public void Image_RejectsTruncatedFrame()
    {
        var frame = new ImageFrameBuffer(new byte[10], 8, 8);

        Assert.False(RapidOcrImage.TryCreate(frame, out var image));
        Assert.Null(image);
    }

    private static ImageFrameBuffer WhiteFrame(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        Array.Fill(pixels, (byte)255);
        return new ImageFrameBuffer(pixels, width, height);
    }
}
