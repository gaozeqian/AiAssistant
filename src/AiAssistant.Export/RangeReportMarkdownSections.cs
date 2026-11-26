using System.Globalization;
using System.Text;

namespace AiAssistant.Export;

internal sealed record AppDistributionItem(string Name, int Count);

internal sealed record ProjectProgressItem(string Name, string Summary, string Duration);

internal sealed record RangeReportSections(
    string Summary,
    IReadOnlyList<AppDistributionItem> Apps,
    IReadOnlyList<string> Highlights,
    IReadOnlyList<ProjectProgressItem> Projects,
    IReadOnlyList<string> Timeline,
    IReadOnlyList<string> Standup);

/// <summary>
/// Extracts the detail sections from the already-redacted report Markdown. Reusing the Markdown
/// as the single source of truth guarantees the PDF and the .md export never disagree and that
/// no unredacted text can leak into the document. Supports both daily (概览/关键成果/项目进展/
/// 时间线/站会视图) and range (总结/每日统计/应用分布/代表性记录) report layouts.
/// </summary>
internal static class RangeReportMarkdownSections
{
    public static RangeReportSections Parse(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return new RangeReportSections(string.Empty, [], [], [], [], []);
        }

        var summary = new List<string>();
        var apps = new List<AppDistributionItem>();
        var highlights = new List<string>();
        var projects = new List<ProjectProgressItem>();
        var timeline = new List<string>();
        var standup = new List<string>();
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
                section = heading switch
                {
                    "总结" or "概览" => "总结",
                    "关键成果" or "代表性记录" => "代表性记录",
                    "每日统计" => "每日统计",
                    "应用分布" => "应用分布",
                    "项目进展" => "项目进展",
                    "时间线" => "时间线",
                    "站会视图" => "站会视图",
                    _ => section == "总结" ? "总结" : string.Empty
                };
                if (section == "总结" && heading is not ("总结" or "概览"))
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
                case "项目进展" when line.StartsWith('|'):
                    if (TryParseProject(line, out var project))
                    {
                        projects.Add(project);
                    }

                    break;
                case "时间线" when line.StartsWith("- ", StringComparison.Ordinal):
                    timeline.Add(line[2..].Trim());
                    break;
                case "站会视图" when line.StartsWith("- ", StringComparison.Ordinal):
                    standup.Add(line[2..].Trim());
                    break;
            }
        }

        return new RangeReportSections(
            string.Join(' ', summary),
            apps,
            highlights,
            projects,
            timeline,
            standup);
    }

    private static bool TryParseProject(string line, out ProjectProgressItem project)
    {
        project = default!;
        var normalized = line.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (normalized.StartsWith("|---", StringComparison.Ordinal))
        {
            return false;
        }

        var cells = line
            .Trim()
            .Trim('|')
            .Split('|')
            .Select(cell => cell.Trim().Replace("\\|", "|", StringComparison.Ordinal))
            .ToArray();
        if (cells.Length < 3 || cells[0] == "项目")
        {
            return false;
        }

        project = new ProjectProgressItem(cells[0], cells[1], cells[2]);
        return true;
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
