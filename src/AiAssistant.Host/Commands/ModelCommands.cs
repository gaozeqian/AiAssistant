using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using AiAssistant.Application.Ports;
using AiAssistant.Application.UseCases;
using AiAssistant.Models;

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
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public bool RequiresDatabaseInitialization => false;

    public bool Matches(string[] args) => CliSupport.HasOption(args, "--benchmark-model");

    public async Task<CliResult> ExecuteAsync(CliCommandContext context, CancellationToken ct)
    {
        var report = await context.Services.ModelBenchmark.RunAsync(ct);
        var format = CliSupport.TryGetOption(context.Args, "--format", out var formatText)
            ? ReportExportFormatExtensions.FromExtension(formatText)
            : ReportExportFormat.Markdown;
        var outputPath = CliSupport.ResolveBenchmarkOutput(context.Args, "model", context.Clock);
        outputPath = Path.ChangeExtension(outputPath, format.ToExtension());
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }
        switch (format)
        {
            case ReportExportFormat.Markdown:
                await File.WriteAllTextAsync(outputPath, report.ToMarkdown(), ct);
                break;
            case ReportExportFormat.Json:
                await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(report, JsonOptions), ct);
                break;
            case ReportExportFormat.Svg:
                await File.WriteAllTextAsync(
                    outputPath,
                    context.Services.DocumentRenderer.RenderSvg(BuildDocument(report)),
                    ct);
                break;
            case ReportExportFormat.Png:
                await File.WriteAllBytesAsync(
                    outputPath,
                    context.Services.DocumentRenderer.RenderPng(BuildDocument(report)),
                    ct);
                break;
            case ReportExportFormat.Pdf:
                await File.WriteAllBytesAsync(
                    outputPath,
                    context.Services.DocumentRenderer.RenderPdf(BuildDocument(report)),
                    ct);
                break;
            default:
                throw new InvalidOperationException($"Unsupported report format: {format}.");
        }

        Console.WriteLine(
            $"Model benchmark: model={report.ChatModel} success={report.SuccessRate:P0} " +
            $"json={report.JsonComplianceRate:P0} avg={report.AverageLatencyMilliseconds}ms");
        Console.WriteLine($"report={outputPath}");
        return CliResult.Done(report.SuccessRate > 0 ? 0 : 1);
    }

    private static ReportDocument BuildDocument(ModelBenchmarkReport report) =>
        new(
            "本机模型基准报告",
            $"模型 {report.ChatModel} · {report.RunAtUtc.ToLocalTime():yyyy-MM-dd HH:mm} · 成功率 {report.SuccessRate.ToString("P1", CultureInfo.InvariantCulture)}",
            [
                new ReportFieldSection(
                    "概览",
                    [
                        new ReportField("模型", report.ChatModel),
                        new ReportField("时间", report.RunAtUtc.ToString("O", CultureInfo.InvariantCulture)),
                        new ReportField("成功率", report.SuccessRate.ToString("P1", CultureInfo.InvariantCulture)),
                        new ReportField("JSON 合规率", report.JsonComplianceRate.ToString("P1", CultureInfo.InvariantCulture)),
                        new ReportField("平均耗时", $"{report.AverageLatencyMilliseconds} ms")
                    ]),
                new ReportBulletSection(
                    "用例",
                    report.Cases.Select(item => string.Create(
                        CultureInfo.InvariantCulture,
                        $"{item.Name} · {(item.Succeeded ? "成功" : "失败")} · JSON {(item.JsonValid ? "合规" : "不合规")} · {item.ResponseCharacters} 字符 · {item.DurationMilliseconds} ms{(item.ErrorType is null ? string.Empty : $" · {item.ErrorType}: {item.ErrorDetail}")}"))
                        .ToArray())
            ]);
}
