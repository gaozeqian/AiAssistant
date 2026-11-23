namespace AiAssistant.Host.Commands;

internal sealed class ProbeModelCommand : ICliCommand
{
    public bool RequiresDatabaseInitialization => false;

    public bool Matches(string[] args) => CliSupport.HasOption(args, "--probe-model");

    public async Task<CliResult> ExecuteAsync(CliCommandContext context, CancellationToken ct)
    {
        var capabilities = await context.Services.ModelCapabilityProber.ProbeAllAsync(
            forceRefresh: true,
            ct);
        if (capabilities.Count == 0)
        {
            Console.WriteLine("No model endpoints configured.");
            return CliResult.Done();
        }

        foreach (var capability in capabilities)
        {
            Console.WriteLine(
                $"{capability.Tier}: chat={capability.ChatAvailable} stream={capability.StreamingAvailable} " +
                $"embed={capability.EmbeddingsAvailable} json={capability.StructuredOutputAvailable} " +
                $"tools={capability.ToolCallingAvailable} model={capability.ChatModel} | {capability.Diagnostic}");
        }

        return CliResult.Done();
    }
}

internal sealed class BenchmarkModelCommand : ICliCommand
{
    public bool RequiresDatabaseInitialization => false;

    public bool Matches(string[] args) => CliSupport.HasOption(args, "--benchmark-model");

    public async Task<CliResult> ExecuteAsync(CliCommandContext context, CancellationToken ct)
    {
        var report = await context.Services.ModelBenchmark.RunAsync(ct);
        var outputPath = CliSupport.ResolveBenchmarkOutput(context.Args, "model", context.Clock);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(outputPath, report.ToMarkdown(), ct);
        Console.WriteLine(
            $"Model benchmark: model={report.ChatModel} success={report.SuccessRate:P0} " +
            $"json={report.JsonComplianceRate:P0} avg={report.AverageLatencyMilliseconds}ms");
        Console.WriteLine($"report={outputPath}");
        return CliResult.Done(report.SuccessRate > 0 ? 0 : 1);
    }
}
