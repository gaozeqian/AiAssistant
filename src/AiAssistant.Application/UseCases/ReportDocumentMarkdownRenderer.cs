using System.Globalization;
using System.Text;
using AiAssistant.Application.Ports;

namespace AiAssistant.Application.UseCases;

/// <summary>Renders a <see cref="ReportDocument"/> as Markdown for exports and CLI output.</summary>
public static class ReportDocumentMarkdownRenderer
{
    public static string Render(ReportDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.InvariantCulture, $"# {document.Title}");
        if (!string.IsNullOrWhiteSpace(document.Subtitle))
        {
            builder.AppendLine();
            builder.AppendLine(document.Subtitle);
        }

        foreach (var section in document.Sections)
        {
            builder.AppendLine();
            builder.AppendLine(CultureInfo.InvariantCulture, $"## {section.Heading}");
            switch (section)
            {
                case ReportParagraphSection paragraphs:
                    foreach (var paragraph in paragraphs.Paragraphs)
                    {
                        builder.AppendLine(paragraph);
                        builder.AppendLine();
                    }

                    break;
                case ReportBulletSection bullets:
                    foreach (var item in bullets.Items)
                    {
                        builder.AppendLine(CultureInfo.InvariantCulture, $"- {item}");
                    }

                    break;
                case ReportFieldSection fields:
                    foreach (var field in fields.Fields)
                    {
                        builder.AppendLine(CultureInfo.InvariantCulture, $"- **{field.Label}**：{field.Value}");
                    }

                    break;
                case ReportCodeSection code:
                    builder.AppendLine("```text");
                    builder.AppendLine(code.Text);
                    builder.AppendLine("```");
                    break;
            }
        }

        if (!string.IsNullOrWhiteSpace(document.Footer))
        {
            builder.AppendLine();
            builder.AppendLine("---");
            builder.AppendLine(document.Footer);
        }

        return builder.ToString();
    }
}
