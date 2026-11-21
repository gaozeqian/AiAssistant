using System.Globalization;
using System.Text;

namespace AiAssistant.Processing.Benchmarks;

public sealed record RecognitionBenchmarkRow(
    string Name,
    string Reference,
    string? Hypothesis,
    double CharacterErrorRate,
    int Edits,
    int ReferenceCharacters,
    TimeSpan Duration,
    string? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>
/// Markdown report shared by the ASR and OCR golden-set commands. Keeps wording stable so
/// version-to-version comparisons stay diffable (S4-R).
/// </summary>
public static class RecognitionBenchmarkReport
{
    private const int SnippetLength = 80;

    public static string Build(
        string title,
        string engine,
        string sampleDirectory,
        IReadOnlyList<RecognitionBenchmarkRow> rows,
        IReadOnlyList<string> warnings,
        DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var succeeded = rows.Where(r => r.Succeeded).ToArray();
        var failed = rows.Count - succeeded.Length;
        var averageCer = succeeded.Length == 0 ? double.NaN : succeeded.Average(r => r.CharacterErrorRate);
        var exactMatches = succeeded.Count(r => r.CharacterErrorRate == 0);

        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.InvariantCulture, $"# {title}");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"- 生成时间：{generatedAt:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- 引擎：{engine}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- 样本目录：{sampleDirectory}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- 样本数：{rows.Count}（成功 {succeeded.Length} / 失败 {failed}）");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"- 平均 CER：{(double.IsNaN(averageCer) ? "n/a" : CharacterErrorRate.ToPercent(averageCer))}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- 完全匹配：{exactMatches}/{succeeded.Length}");
        builder.AppendLine();

        if (rows.Count == 0)
        {
            builder.AppendLine("没有可用样本。请按 `<name>.wav` + `<name>.txt`（OCR 为图片 + `.txt`）放置外挂模型与语料。");
        }
        else
        {
            builder.AppendLine("## 逐样本");
            builder.AppendLine();
            builder.AppendLine("| 样本 | CER | 编辑/字数 | 耗时 | 识别结果 | 参考答案 | 错误 |");
            builder.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var row in rows)
            {
                builder.Append("| ").Append(Escape(row.Name));
                builder.Append(" | ").Append(row.Succeeded ? CharacterErrorRate.ToPercent(row.CharacterErrorRate) : "n/a");
                builder.Append(" | ").Append(row.Succeeded
                    ? $"{row.Edits.ToString(CultureInfo.InvariantCulture)}/{row.ReferenceCharacters.ToString(CultureInfo.InvariantCulture)}"
                    : "n/a");
                builder.Append(" | ").Append(row.Duration.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)).Append(" ms");
                builder.Append(" | ").Append(Snippet(row.Hypothesis));
                builder.Append(" | ").Append(Snippet(row.Reference));
                builder.Append(" | ").Append(Escape(row.Error ?? string.Empty));
                builder.AppendLine(" |");
            }
        }

        if (warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## 警告");
            builder.AppendLine();
            foreach (var warning in warnings)
            {
                builder.Append("- ").AppendLine(Escape(warning));
            }
        }

        return builder.ToString();
    }

    public static string Snippet(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var singleLine = text.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        return singleLine.Length <= SnippetLength
            ? Escape(singleLine)
            : Escape(singleLine[..SnippetLength]) + "…";
    }

    private static string Escape(string text) => text.Replace("|", "\\|", StringComparison.Ordinal);
}
