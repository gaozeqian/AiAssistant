namespace AiAssistant.Host.Commands;

internal sealed class ReportRangeCommand : ICliCommand
{
    public bool RequiresDatabaseInitialization => true;

    public bool Matches(string[] args) => CliSupport.HasOption(args, "--report-range");

    public async Task<CliResult> ExecuteAsync(CliCommandContext context, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(context.Clock.LocalNow.LocalDateTime.Date);
        var from = CliSupport.ParseDateOption(context.Args, "--from", today.AddDays(-6));
        var to = CliSupport.ParseDateOption(context.Args, "--to", today);
        var outputDirectory = CliSupport.ResolveOutputDirectory(context.Args, "reports");
        var report = await context.Services.RangeReport.GenerateAsync(from, to, ct);
        var export = await context.Services.RangeReport.ExportAsync(report, outputDirectory, ct);
        Console.WriteLine(
            $"Range report: from={from:yyyy-MM-dd} to={to:yyyy-MM-dd} events={report.TotalEvents} " +
            $"model={report.ModelId} degraded={report.Degraded}");
        Console.WriteLine($"markdown={export.MarkdownPath}");
        Console.WriteLine($"svg={export.SvgPath}");
        Console.WriteLine($"stats={export.StatsPath}");
        if (export.PngPath is not null)
        {
            Console.WriteLine($"png={export.PngPath}");
        }

        if (export.PdfPath is not null)
        {
            Console.WriteLine($"pdf={export.PdfPath}");
        }

        return CliResult.Done();
    }
}

internal sealed class AskCommand : ICliCommand
{
    public bool RequiresDatabaseInitialization => true;

    public bool Matches(string[] args) => CliSupport.HasOption(args, "--ask");

    public async Task<CliResult> ExecuteAsync(CliCommandContext context, CancellationToken ct)
    {
        CliSupport.TryGetOption(context.Args, "--ask", out var question);
        var days = CliSupport.ParseIntOption(context.Args, "--days", 7, 1, 366);
        var answer = await context.Services.EvidenceSearch.AskRecentAsync(question, days, ct);
        Console.WriteLine(answer.Answer);
        Console.WriteLine(
            $"citations={string.Join(",", answer.CitedEvidenceIds)} model={answer.ModelId} degraded={answer.Degraded}");
        foreach (var evidence in answer.Evidence)
        {
            Console.WriteLine(
                $"[{evidence.EvidenceId}] {evidence.LocalDate:yyyy-MM-dd} {evidence.AppName} | {evidence.Title}");
        }

        if (CliSupport.TryGetOption(context.Args, "--output", out var exportDirectory))
        {
            var path = await context.Services.EvidenceSearch.ExportAnswerAsync(
                answer,
                Path.GetFullPath(exportDirectory),
                ct);
            Console.WriteLine($"export={path}");
        }

        return CliResult.Done(answer.HasEvidence ? 0 : 1);
    }
}
