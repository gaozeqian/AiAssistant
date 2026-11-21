namespace AiAssistant.Processing.Ocr;

/// <summary>Greedy CTC decoding for PP-OCR recognition heads (index 0 is the blank label).</summary>
public static class RapidOcrCtcDecoder
{
    public static (string Text, double Confidence) GreedyDecode(
        ReadOnlySpan<float> logits,
        int timeSteps,
        int classCount,
        IReadOnlyList<string> dictionary)
    {
        if (timeSteps <= 0 || classCount <= 0 || logits.Length < timeSteps * classCount)
        {
            return (string.Empty, 0);
        }

        var characters = new System.Text.StringBuilder();
        var probabilitySum = 0d;
        var emitted = 0;
        var previousIndex = -1;

        for (var t = 0; t < timeSteps; t++)
        {
            var offset = t * classCount;
            var bestIndex = 0;
            var bestProbability = logits[offset];
            for (var c = 1; c < classCount; c++)
            {
                var probability = logits[offset + c];
                if (probability > bestProbability)
                {
                    bestProbability = probability;
                    bestIndex = c;
                }
            }

            if (bestIndex != 0 && bestIndex != previousIndex && bestIndex - 1 < dictionary.Count)
            {
                characters.Append(dictionary[bestIndex - 1]);
                probabilitySum += bestProbability;
                emitted++;
            }

            previousIndex = bestIndex;
        }

        return emitted == 0
            ? (string.Empty, 0)
            : (characters.ToString(), probabilitySum / emitted);
    }
}
