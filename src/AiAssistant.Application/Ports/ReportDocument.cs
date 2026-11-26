namespace AiAssistant.Application.Ports;

/// <summary>
/// Backend-neutral document used by Q&amp;A, diagnostics and benchmark exports. Keeping the content
/// structured (instead of pre-rendered Markdown) lets every format share the same text and metadata.
/// </summary>
public sealed record ReportDocument(
    string Title,
    string Subtitle,
    IReadOnlyList<ReportSection> Sections,
    string Footer = "由 AiAssistant 本地生成 · 数据已脱敏");

public abstract record ReportSection(string Heading);

public sealed record ReportParagraphSection(string Heading, IReadOnlyList<string> Paragraphs) : ReportSection(Heading);

public sealed record ReportBulletSection(string Heading, IReadOnlyList<string> Items) : ReportSection(Heading);

public sealed record ReportFieldSection(string Heading, IReadOnlyList<ReportField> Fields) : ReportSection(Heading);

public sealed record ReportCodeSection(string Heading, string Text) : ReportSection(Heading);

public sealed record ReportField(string Label, string Value);
