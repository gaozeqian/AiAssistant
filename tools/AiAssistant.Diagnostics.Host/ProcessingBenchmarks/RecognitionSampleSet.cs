using System.Text;

namespace AiAssistant.Processing.Benchmarks;

public sealed record RecognitionSample(
    string Name,
    string MediaPath,
    string ReferencePath,
    string ReferenceText);

public sealed record RecognitionSampleSetResult(
    IReadOnlyList<RecognitionSample> Samples,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Discovers golden-set samples: each media file (wav/png/...) with a sibling UTF-8 `.txt`
/// holding the ground-truth transcript or text (S4-R).
/// </summary>
public static class RecognitionSampleSet
{
    private static readonly string[] DefaultMediaExtensions = [".wav"];

    public static RecognitionSampleSetResult Load(string directory, params string[] mediaExtensions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var extensions = mediaExtensions.Length == 0 ? DefaultMediaExtensions : mediaExtensions;

        var warnings = new List<string>();
        if (!Directory.Exists(directory))
        {
            warnings.Add($"sample directory not found: {directory}");
            return new RecognitionSampleSetResult([], warnings);
        }

        var extensionSet = extensions
            .Select(e => e.StartsWith('.') ? e : "." + e)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var samples = new List<RecognitionSample>();
        foreach (var mediaPath in Directory
                     .EnumerateFiles(directory)
                     .Where(path => extensionSet.Contains(Path.GetExtension(path)))
                     .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileNameWithoutExtension(mediaPath);
            var referencePath = Path.ChangeExtension(mediaPath, ".txt");
            if (!File.Exists(referencePath))
            {
                warnings.Add($"{name}: reference text missing ({Path.GetFileName(referencePath)})");
                continue;
            }

            var referenceText = File.ReadAllText(referencePath, Encoding.UTF8).Trim().TrimStart('\uFEFF');
            if (referenceText.Length == 0)
            {
                warnings.Add($"{name}: reference text is empty");
                continue;
            }

            samples.Add(new RecognitionSample(name, mediaPath, referencePath, referenceText));
        }

        if (samples.Count == 0 && warnings.Count == 0)
        {
            warnings.Add($"no samples with extensions {string.Join(", ", extensionSet)} in {directory}");
        }

        return new RecognitionSampleSetResult(samples, warnings);
    }
}
