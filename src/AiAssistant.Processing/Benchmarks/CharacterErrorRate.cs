using System.Globalization;
using System.Text;

namespace AiAssistant.Processing.Benchmarks;

/// <summary>
/// Character error rate for Chinese/English transcripts (S4-R golden set).
/// Normalization drops whitespace, punctuation and symbols so that ASR/OCR formatting
/// differences do not dominate the score; casing is folded for Latin text.
/// </summary>
public static class CharacterErrorRate
{
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch) || char.IsSymbol(ch) || char.IsControl(ch))
            {
                continue;
            }

            builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString();
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
