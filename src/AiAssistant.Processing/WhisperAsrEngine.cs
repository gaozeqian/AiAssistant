using System.Text;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Configuration;
using AiAssistant.Core.ValueObjects;
using Microsoft.Extensions.Logging;
using Whisper.net;

namespace AiAssistant.Processing;

/// <summary>
/// High-quality batch ASR adapter. Model assets stay outside Git; a missing file degrades the
/// capability instead of failing startup, and sherpa-onnx remains the default command engine.
/// </summary>
public sealed class WhisperAsrOptions
{
    public string ModelPath { get; init; } = string.Empty;
    public string Language { get; init; } = "zh";
    public int Threads { get; init; } = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2));
    public string ModelVersion { get; init; } = "whisper.net-ggml";

    public bool HasModel => !string.IsNullOrWhiteSpace(ModelPath) && File.Exists(ModelPath);
}

public sealed class WhisperAsrEngine : IAsrEngine
{
    private readonly WhisperAsrOptions _options;
    private readonly ILogger<WhisperAsrEngine> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WhisperFactory? _factory;
    private string? _errorType;
    private bool _disposed;

    public WhisperAsrEngine(WhisperAsrOptions options, ILogger<WhisperAsrEngine> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    public CapabilityState Capability => _errorType is not null
        ? CapabilityState.Degraded(CapabilityKind.AudioCapture, $"Whisper batch ASR degraded ({_errorType})")
        : _options.HasModel
            ? CapabilityState.Supported(
                CapabilityKind.AudioCapture,
                $"Whisper.net batch ASR ({_options.Language}, {_options.ModelVersion})")
            : CapabilityState.PermissionRequired(
                CapabilityKind.AudioCapture,
                "Whisper model file is not installed",
                "Set AIASSISTANT_WHISPER_MODEL_PATH to a ggml Whisper model such as ggml-small.bin.");

    public async Task<AudioTranscriptionResult> TranscribeAsync(
        AudioTranscriptionRequest request,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_options.HasModel || request.SampleRateHz != 16_000 || request.Samples.Length == 0)
        {
            return AudioTranscriptionResult.Empty;
        }

        await _gate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            var factory = GetFactory();
            using var processor = factory.CreateBuilder()
                .WithLanguage(_options.Language)
                .WithThreads(_options.Threads)
                .WithProbabilities()
                .Build();

            var text = new StringBuilder();
            double probabilitySum = 0;
            var segmentCount = 0;
            await foreach (var segment in processor.ProcessAsync(request.Samples, ct))
            {
                text.Append(segment.Text);
                probabilitySum += segment.Probability;
                segmentCount++;
            }

            var transcript = text.ToString().Trim();
            _errorType = null;
            return new AudioTranscriptionResult(
                transcript,
                transcript.Length == 0 || segmentCount == 0 ? 0 : probabilitySum / segmentCount,
                _options.ModelVersion);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _errorType = ex.GetType().Name;
            _logger.LogWarning("Whisper ASR degraded with {ExceptionType}.", ex.GetType().Name);
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
            _factory?.Dispose();
            _factory = null;
            _gate.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private WhisperFactory GetFactory() => _factory ??= WhisperFactory.FromPath(_options.ModelPath);
}

/// <summary>Explicit engine choice; sherpa-onnx stays the default real-time command engine.</summary>
public enum AsrEngineKind
{
    Sherpa = 0,
    Whisper = 1
}

public static class AsrEngineSelection
{
    public const string EnvironmentVariableName = EnvironmentNames.AsrEngine;

    public static AsrEngineKind Parse(string? value) =>
        string.Equals(value, "whisper", StringComparison.OrdinalIgnoreCase)
            ? AsrEngineKind.Whisper
            : AsrEngineKind.Sherpa;
}
