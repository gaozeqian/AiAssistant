using System.Globalization;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Processing;

public abstract class ProcessingStageBase(
    string name,
    TimeSpan defaultTimeout,
    params string[] resourceClaims) : IProcessingStage<ProcessingEnvelope, ProcessingEnvelope>
{
    public string StageName { get; } = name;
    public string StageVersion => "1.0";
    public TimeSpan DefaultTimeout { get; } = defaultTimeout;
    public IReadOnlyCollection<string> ResourceClaims { get; } = resourceClaims;

    public abstract Task<ProcessingEnvelope> ProcessAsync(ProcessingEnvelope input, CancellationToken ct);
}

/// <summary>Marks near-duplicate frames so expensive OCR/ASR stages can skip them without dropping the raw event.</summary>
public sealed class PerceptualDeduplicationStage(TimeSpan? window = null)
    : ProcessingStageBase("dedupe", TimeSpan.FromMilliseconds(100), "cpu")
{
    private readonly TimeSpan _window = window ?? TimeSpan.FromSeconds(10);
    private readonly Lock _sync = new();
    private readonly Dictionary<string, (ulong Hash, DateTimeOffset SeenAtUtc)> _recent = new(StringComparer.Ordinal);

    public override Task<ProcessingEnvelope> ProcessAsync(ProcessingEnvelope input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (input.Event.PerceptualHash is not { } hash)
        {
            return Task.FromResult(input);
        }

        var key = $"{input.Event.AppName}\u001F{input.Event.WindowTitle}";
        var now = input.Event.Timestamp.OccurredAtUtc;
        var duplicate = false;
        lock (_sync)
        {
            if (_recent.TryGetValue(key, out var previous) &&
                now - previous.SeenAtUtc <= _window &&
                PerceptualHash.IsNearDuplicate(hash, previous.Hash))
            {
                duplicate = true;
            }

            if (_recent.Count >= 256 && !_recent.ContainsKey(key))
            {
                _recent.Remove(_recent.Keys.First());
            }

            _recent[key] = (hash, now);
        }

        return Task.FromResult(input with { IsDuplicate = duplicate });
    }
}

/// <summary>Accessibility-first text extraction with platform OCR fallback through the unified port.</summary>
public sealed class TextExtractionStage(ITextExtractor extractor)
    : ProcessingStageBase("text-extraction", TimeSpan.FromSeconds(2), "accessibility", "ocr")
{
    public override async Task<ProcessingEnvelope> ProcessAsync(ProcessingEnvelope input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (input.IsDuplicate ||
            !string.IsNullOrWhiteSpace(input.Text) ||
            (input.Request.NativeWindowHandle == 0 && input.ScreenshotPng is not { Length: > 0 }))
        {
            return input;
        }

        var result = await extractor.ExtractAsync(
            new TextExtractionRequest(
                input.Request.NativeWindowHandle,
                input.Event.AppName,
                input.Event.WindowTitle,
                input.ScreenshotPng),
            ct);

        if (result.Method == TextExtractionMethod.None || string.IsNullOrWhiteSpace(result.Text))
        {
            return input.WithMetadata("processing.text_method", "none");
        }

        var method = result.Method == TextExtractionMethod.AccessibilityTree ? "accessibility" : "ocr";
        return (input with { Text = result.Text })
            .WithMetadata("processing.text_method", method)
            .WithMetadata("processing.text_confidence", result.Confidence.ToString("F2", CultureInfo.InvariantCulture));
    }
}

/// <summary>Always-on local redaction stage. It never depends on a model or network availability.</summary>
public sealed class RedactionStage(SensitiveDataRedactor? redactor = null)
    : ProcessingStageBase("redaction", TimeSpan.FromMilliseconds(300), "cpu")
{
    private readonly SensitiveDataRedactor _redactor = redactor ?? new SensitiveDataRedactor();

    public override Task<ProcessingEnvelope> ProcessAsync(ProcessingEnvelope input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var text = _redactor.Redact(input.Text);
        var transcript = _redactor.Redact(input.Transcript);
        var count = text.RedactionCount + transcript.RedactionCount;
        var output = input with
        {
            Text = text.Text,
            Transcript = transcript.Text
        };
        return Task.FromResult(count == 0
            ? output
            : output.WithMetadata("processing.redactions", count.ToString(CultureInfo.InvariantCulture)));
    }
}

public sealed class AudioTranscriptionStage(IAsrEngine? asrEngine, int minimumSamples = 1600)
    : ProcessingStageBase("asr", TimeSpan.FromSeconds(8), "asr", "cpu")
{
    public override async Task<ProcessingEnvelope> ProcessAsync(ProcessingEnvelope input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (asrEngine is null ||
            input.Audio is not { } audio ||
            audio.Samples.Length < minimumSamples ||
            !asrEngine.Capability.IsOperational)
        {
            return input;
        }

        var result = await asrEngine.TranscribeAsync(
            new AudioTranscriptionRequest(audio.Samples, audio.SampleRateHz),
            ct);
        if (string.IsNullOrWhiteSpace(result.Text))
        {
            return input.WithMetadata("processing.asr", "empty");
        }

        return (input with
        {
            Transcript = result.Text,
            AsrConfidence = result.Confidence,
            AsrModelVersion = result.ModelVersion
        })
            .WithMetadata("processing.asr_model", result.ModelVersion)
            .WithMetadata("processing.asr_confidence", result.Confidence.ToString("F3", CultureInfo.InvariantCulture));
    }
}

public sealed class SpeakerDiarizationStage(ISpeakerDiarizationEngine? engine, int minimumSamples = 8000)
    : ProcessingStageBase("diarization", TimeSpan.FromSeconds(10), "diarization", "cpu")
{
    public override async Task<ProcessingEnvelope> ProcessAsync(ProcessingEnvelope input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (engine is null ||
            input.Audio is not { } audio ||
            audio.Samples.Length < minimumSamples ||
            !engine.Capability.IsOperational)
        {
            return input;
        }

        var turns = await engine.DiarizeAsync(audio.Samples, audio.SampleRateHz, ct);
        if (turns.Count == 0)
        {
            return input.WithMetadata("processing.speakers", "0");
        }

        return (input with { Speakers = turns })
            .WithMetadata("processing.speakers", turns.Select(t => t.SpeakerId).Distinct(StringComparer.Ordinal).Count().ToString(CultureInfo.InvariantCulture))
            .WithMetadata("processing.diarization_model", engine.ModelVersion);
    }
}

/// <summary>
/// Deterministic no-model fallback for environments without diarization assets.
/// It segments by energy gaps and labels alternating turns, and is always reported as Degraded.
/// </summary>
public sealed class EnergyTurnDiarizationEngine(int minimumTurnMs = 800) : ISpeakerDiarizationEngine
{
    public CapabilityState Capability => CapabilityState.Degraded(
        CapabilityKind.AudioCapture,
        "Energy-gap speaker turn heuristic (no embedding model)",
        "请安装说话人嵌入模型与 sherpa-onnx 说话人分离以提升准确度。");

    public string ModelVersion => "energy-turn-v1";

    public Task<IReadOnlyList<SpeakerTurn>> DiarizeAsync(
        ReadOnlyMemory<float> samples,
        int sampleRateHz,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (sampleRateHz <= 0 || samples.Length == 0)
        {
            return Task.FromResult<IReadOnlyList<SpeakerTurn>>([]);
        }
        var window = Math.Max(1, sampleRateHz / 10);
        var turns = new List<SpeakerTurn>();
        var speaker = 0;
        var segmentStart = 0;
        var silenceWindows = 0;
        var minSilenceWindows = Math.Max(1, minimumTurnMs / 100);

        for (var offset = 0; offset + window <= samples.Length; offset += window)
        {
            var energy = 0.0;
            for (var i = 0; i < window; i++)
            {
                energy += samples.Span[offset + i] * samples.Span[offset + i];
            }

            var rms = Math.Sqrt(energy / window);
            if (rms < 0.01)
            {
                silenceWindows++;
                if (silenceWindows >= minSilenceWindows && offset > segmentStart)
                {
                    turns.Add(new SpeakerTurn($"speaker_{speaker}", Seconds(segmentStart), Seconds(offset)));
                    speaker = 1 - speaker;
                    segmentStart = offset + window;
                }
            }
            else
            {
                silenceWindows = 0;
            }
        }

        if (segmentStart < samples.Length)
        {
            turns.Add(new SpeakerTurn($"speaker_{speaker}", Seconds(segmentStart), Seconds(samples.Length)));
        }

        return Task.FromResult<IReadOnlyList<SpeakerTurn>>(turns);

        TimeSpan Seconds(int sampleIndex) => TimeSpan.FromSeconds((double)sampleIndex / sampleRateHz);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
