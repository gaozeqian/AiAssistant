using System.Globalization;
using System.Text;

namespace AiAssistant.Export;

internal sealed record AppDistributionItem(string Name, int Count);

internal sealed record RangeReportSections(
    string Summary,
    IReadOnlyList<AppDistributionItem> Apps,
    IReadOnlyList<string> Highlights);

/// <summary>
/// Extracts the detail sections from the already-redacted report Markdown. Reusing the Markdown
/// as the single source of truth guarantees the PDF and the .md export never disagree and that
/// no unredacted text can leak into the document.
/// </summary>
internal static class RangeReportMarkdownSections
{
    public static RangeReportSections Parse(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return new RangeReportSections(string.Empty, [], []);
        }

        var summary = new List<string>();
        var apps = new List<AppDistributionItem>();
        var highlights = new List<string>();
        var section = string.Empty;

        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line == "---")
            {
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                var heading = line[3..].Trim();
                if (heading is "总结" or "每日统计" or "应用分布" or "代表性记录")
                {
                    section = heading;
                }
                else if (section == "总结")
                {
                    // Model-written subheading: keep it as body text instead of losing content.
                    summary.Add(line);
                }

                continue;
            }

            switch (section)
            {
                case "总结":
                    summary.Add(line);
                    break;
                case "应用分布" when line.StartsWith("- ", StringComparison.Ordinal):
                    apps.Add(ParseApp(line[2..].Trim()));
                    break;
                case "代表性记录" when line.StartsWith("- ", StringComparison.Ordinal):
                    highlights.Add(line[2..].Trim());
                    break;
            }
        }

        return new RangeReportSections(string.Join(' ', summary), apps, highlights);
    }

    private static AppDistributionItem ParseApp(string text)
    {
        var colon = text.LastIndexOf(':');
        if (colon <= 0)
        {
            return new AppDistributionItem(text, 0);
        }

        var countText = text[(colon + 1)..].Replace("条", string.Empty, StringComparison.Ordinal).Trim();
        return int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
            ? new AppDistributionItem(text[..colon].Trim(), count)
            : new AppDistributionItem(text, 0);
    }
}
