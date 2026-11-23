using System.Diagnostics;

namespace AiAssistant.Processing.Benchmarks;

/// <summary>
/// Scores a golden set by running each sample through a recognizer delegate. Keeping the loop here
/// (instead of in the CLI command) makes CER aggregation and the "model unavailable" path testable
/// without model assets (S4-R).
/// </summary>
public static class RecognitionBenchmarkEvaluator
{
    public static async Task<IReadOnlyList<RecognitionBenchmarkRow>> RunAsync(
        IReadOnlyList<RecognitionSample> samples,
        Func<RecognitionSample, CancellationToken, Task<string>> recognize,
        string? unavailableReason,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(recognize);

        var rows = new List<RecognitionBenchmarkRow>(samples.Count);
        foreach (var sample in samples)
        {
            var referenceNormalized = CharacterErrorRate.Normalize(sample.ReferenceText);
            if (unavailableReason is not null)
            {
                // A missing model must not be scored as a 100% CER failure of the recognizer.
                rows.Add(new RecognitionBenchmarkRow(
                    sample.Name,
                    sample.ReferenceText,
                    Hypothesis: null,
                    0,
                    0,
                    referenceNormalized.Length,
                    TimeSpan.Zero,
                    unavailableReason));
                continue;
            }

            var stopwatch = Stopwatch.StartNew();
            try
            {
                var hypothesis = await recognize(sample, ct);
                stopwatch.Stop();

                var hypothesisNormalized = CharacterErrorRate.Normalize(hypothesis);
                rows.Add(new RecognitionBenchmarkRow(
                    sample.Name,
                    sample.ReferenceText,
                    hypothesis,
                    CharacterErrorRate.Compute(sample.ReferenceText, hypothesis),
                    CharacterErrorRate.ComputeEdits(referenceNormalized, hypothesisNormalized),
                    referenceNormalized.Length,
                    stopwatch.Elapsed,
                    Error: null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                stopwatch.Stop();
                rows.Add(new RecognitionBenchmarkRow(
                    sample.Name,
                    sample.ReferenceText,
                    Hypothesis: null,
                    0,
                    0,
                    referenceNormalized.Length,
                    stopwatch.Elapsed,
                    $"{ex.GetType().Name}: {ex.Message}"));
            }
        }

        return rows;
    }

    public static string AverageCer(IReadOnlyList<RecognitionBenchmarkRow> rows)
    {
        var succeeded = rows.Where(r => r.Succeeded).ToArray();
        return succeeded.Length == 0
            ? "n/a"
            : CharacterErrorRate.ToPercent(succeeded.Average(r => r.CharacterErrorRate));
    }
}
