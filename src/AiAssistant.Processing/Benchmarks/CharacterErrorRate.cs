using System.Globalization;
using System.Text;

namespace AiAssistant.Processing.Benchmarks;

/// <summary>
/// Character error rate for Chinese/English transcripts (S4-R golden set).
/// Normalization drops whitespace, punctuation and symbols, folds Latin casing, and converts
/// Traditional to Simplified Chinese (OpenCC TSCharacters, Apache-2.0) so that Whisper's
/// Traditional output is not mis-scored as recognition errors. Arabic numerals are kept as-is:
/// "2012" vs "二零一二" is a formatting difference that still counts, and is called out in reports.
/// </summary>
internal static class CharacterErrorRate
{
    private static readonly Lazy<IReadOnlyDictionary<char, char>> TraditionalToSimplified =
        new(LoadTraditionalToSimplified, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var map = TraditionalToSimplified.Value;
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch) || char.IsSymbol(ch) || char.IsControl(ch))
            {
                continue;
            }

            var normalized = map.TryGetValue(ch, out var simplified) ? simplified : ch;
            builder.Append(char.ToLowerInvariant(normalized));
        }

        return builder.ToString();
    }

    private static Dictionary<char, char> LoadTraditionalToSimplified()
    {
        try
        {
            using var stream = typeof(CharacterErrorRate).Assembly.GetManifestResourceStream(
                "AiAssistant.Processing.Benchmarks.TSCharacters.txt");
            if (stream is null)
            {
                return new Dictionary<char, char>(0);
            }

            var map = new Dictionary<char, char>(4096);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length < 3 || line[0] == '#')
                {
                    continue;
                }

                var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || parts[0].Length != 1)
                {
                    continue;
                }

                var target = parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (string.IsNullOrEmpty(target) || target.Length != 1)
                {
                    continue;
                }

                map[parts[0][0]] = target[0];
            }

            return map;
        }
        catch (Exception)
        {
            // The mapping is an optional normalization resource; never break metric computation.
            return new Dictionary<char, char>(0);
        }
    }

    /// <summary>Levenshtein distance over normalized characters.</summary>
    public static int ComputeEdits(string reference, string hypothesis)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(hypothesis);

        if (reference.Length == 0)
        {
            return hypothesis.Length;
        }

        if (hypothesis.Length == 0)
        {
            return reference.Length;
        }

        var previous = new int[hypothesis.Length + 1];
        var current = new int[hypothesis.Length + 1];
        for (var j = 0; j <= hypothesis.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= reference.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= hypothesis.Length; j++)
            {
                var cost = reference[i - 1] == hypothesis[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[hypothesis.Length];
    }

    /// <summary>
    /// Returns CER as edits / reference characters. 0 means an exact match after normalization;
    /// values above 1 are possible when the hypothesis inserts many extra characters.
    /// An empty reference scores 0 for an empty hypothesis and 1 otherwise.
    /// </summary>
    public static double Compute(string reference, string hypothesis)
    {
        var normalizedReference = Normalize(reference);
        var normalizedHypothesis = Normalize(hypothesis);
        if (normalizedReference.Length == 0)
        {
            return normalizedHypothesis.Length == 0 ? 0 : 1;
        }

        var edits = ComputeEdits(normalizedReference, normalizedHypothesis);
        return edits / (double)normalizedReference.Length;
    }

    public static string ToPercent(double cer) =>
        (cer * 100).ToString("F2", CultureInfo.InvariantCulture) + "%";
}
