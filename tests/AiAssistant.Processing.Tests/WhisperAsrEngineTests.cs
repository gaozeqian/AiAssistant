using AiAssistant.Application.Ports;
using AiAssistant.Core.Configuration;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Processing;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiAssistant.Processing.Tests;

public sealed class WhisperAsrEngineTests
{
    [Fact]
    public async Task MissingModel_ReportsPermissionRequired_AndSkipsTranscription()
    {
        var modelPath = Path.Combine(
            AppContext.BaseDirectory,
            "test-temp",
            Guid.NewGuid().ToString("N"),
            "ggml-small.bin");
        await using var engine = new WhisperAsrEngine(
            new WhisperAsrOptions { ModelPath = modelPath },
            NullLogger<WhisperAsrEngine>.Instance);

        Assert.Equal(CapabilityStatus.PermissionRequired, engine.Capability.Status);
        Assert.Contains(EnvironmentNames.WhisperModelPath, engine.Capability.RemediationHint, StringComparison.Ordinal);

        var result = await engine.TranscribeAsync(
            new AudioTranscriptionRequest(new float[1600], 16_000),
            CancellationToken.None);

        Assert.Equal(AudioTranscriptionResult.Empty, result);
        Assert.Equal(CapabilityStatus.PermissionRequired, engine.Capability.Status);
    }

    [Fact]
    public async Task Non16kAudio_ReturnsEmptyWithoutTouchingModel()
    {
        var modelDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "test-temp",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(modelDirectory);
        var modelPath = Path.Combine(modelDirectory, "ggml-small.bin");
        await File.WriteAllBytesAsync(modelPath, new byte[8]);

        await using var engine = new WhisperAsrEngine(
            new WhisperAsrOptions { ModelPath = modelPath },
            NullLogger<WhisperAsrEngine>.Instance);

        Assert.Equal(CapabilityStatus.Supported, engine.Capability.Status);

        var result = await engine.TranscribeAsync(
            new AudioTranscriptionRequest(new float[4800], 48_000),
            CancellationToken.None);

        Assert.Equal(AudioTranscriptionResult.Empty, result);
        Assert.Equal(CapabilityStatus.Supported, engine.Capability.Status);

        Directory.Delete(modelDirectory, recursive: true);
    }

    [Fact]
    public void NativeRuntime_LoadsInThisHost()
    {
        var info = Whisper.net.WhisperFactory.GetRuntimeInfo();

        Assert.False(string.IsNullOrWhiteSpace(info));
    }

    [Theory]
    [InlineData(null, AsrEngineKind.Sherpa)]
    [InlineData("", AsrEngineKind.Sherpa)]
    [InlineData("sherpa", AsrEngineKind.Sherpa)]
    [InlineData("whisper", AsrEngineKind.Whisper)]
    [InlineData("WHISPER", AsrEngineKind.Whisper)]
    public void AsrEngineSelection_OnlySwitchesOnExplicitWhisperValue(string? value, AsrEngineKind expected)
    {
        Assert.Equal(expected, AsrEngineSelection.Parse(value));
        Assert.Equal(EnvironmentNames.AsrEngine, AsrEngineSelection.EnvironmentVariableName);
    }
}
