using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;

namespace AiAssistant.Models;

public sealed record ModelBenchmarkCase(
    string Name,
    string Prompt,
    BenchmarkExpectation Expectation);

public enum BenchmarkExpectation
{
    NonEmptyChinese,
    ValidJson,
    SemanticLabels
}

public sealed record ModelBenchmarkCaseResult(
    string Name,
    bool Succeeded,
    bool JsonValid,
    int ResponseCharacters,
    long DurationMilliseconds,
    string? ErrorType,
    string? ErrorDetail = null);

public sealed record ModelBenchmarkReport(
    string ChatModel,
    DateTimeOffset RunAtUtc,
    IReadOnlyList<ModelBenchmarkCaseResult> Cases,
    double SuccessRate,
    double JsonComplianceRate,
    long AverageLatencyMilliseconds)
{
    public string ToMarkdown()
    {
        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.InvariantCulture, $"# 本机模型基准报告");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"- 模型：`{ChatModel}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- 时间：{RunAtUtc:O}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- 成功率：{SuccessRate:P1}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- JSON 合规率：{JsonComplianceRate:P1}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- 平均耗时：{AverageLatencyMilliseconds} ms");
        builder.AppendLine();
        builder.AppendLine("| 用例 | 成功 | JSON | 字符数 | 耗时 | 错误 |");
        builder.AppendLine("|---|---|---|---|---|---|");
        foreach (var item in Cases)
        {
            var error = item.ErrorType is null
                ? "-"
                : string.IsNullOrWhiteSpace(item.ErrorDetail)
                    ? item.ErrorType
                    : $"{item.ErrorType}: {item.ErrorDetail}";
            if (error.Length > 160)
            {
                error = error[..160];
            }

            builder.AppendLine(CultureInfo.InvariantCulture, $"| {item.Name} | {(item.Succeeded ? "是" : "否")} | {(item.JsonValid ? "是" : "否")} | {item.ResponseCharacters} | {item.DurationMilliseconds} ms | {error} |");
        }

        return builder.ToString();
    }
}

/// <summary>Fixed synthetic Chinese benchmark set; no personal activity data is included.</summary>
public sealed class ModelBenchmarkRunner(
    IModelGateway model,
    ModelEndpointTier tier = ModelEndpointTier.LocalOffline,
    IClock? clock = null)
{
    private readonly IClock _clock = clock ?? new SystemClock();
    public static IReadOnlyList<ModelBenchmarkCase> Cases { get; } =
    [
        new(
            "中文摘要",
            "用两句话总结：上午修复登录超时，下午完成支付回调测试并提交发布说明。",
            BenchmarkExpectation.NonEmptyChinese),
        new(
            "语义标签",
            "为以下活动输出不超过 5 个中文标签，逗号分隔：代码评审、数据库迁移、团队周会。",
            BenchmarkExpectation.SemanticLabels),
        new(
            "结构化 JSON",
            """只输出 JSON：{"overview":"总结","highlights":["成果"],"standup":{"today":"计划"}}""",
            BenchmarkExpectation.ValidJson)
    ];

    public async Task<ModelBenchmarkReport> RunAsync(CancellationToken ct)
    {
        var results = new List<ModelBenchmarkCaseResult>(Cases.Count);
        var modelId = model.ChatModelId;
        foreach (var testCase in Cases)
        {
            ct.ThrowIfCancellationRequested();
            var watch = Stopwatch.StartNew();
            try
            {
                var response = await model.CompleteAsync(
                    new ModelCompletionRequest(
                        "你是本机离线模型基准测试器。只输出用户要求的内容。",
                        testCase.Prompt,
                        Tier: tier,
                        Purpose: "model-benchmark",
                        Temperature: 0),
                    ct);
                watch.Stop();
                modelId = response.ModelId;
                var jsonValid = testCase.Expectation != BenchmarkExpectation.ValidJson || IsValidJson(response.Text);
                var succeeded = !string.IsNullOrWhiteSpace(response.Text) && jsonValid;
                results.Add(new ModelBenchmarkCaseResult(
                    testCase.Name,
                    succeeded,
                    jsonValid,
                    response.Text.Length,
                    watch.ElapsedMilliseconds,
                    null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                watch.Stop();
                results.Add(new ModelBenchmarkCaseResult(
                    testCase.Name,
                    false,
                    false,
                    0,
                    watch.ElapsedMilliseconds,
                    ex.GetType().Name,
                    ex.Message));
            }
        }

        var successRate = results.Count == 0 ? 0 : results.Count(item => item.Succeeded) / (double)results.Count;
        var jsonCases = results.Where((_, index) => Cases[index].Expectation == BenchmarkExpectation.ValidJson).ToArray();
        var jsonRate = jsonCases.Length == 0 ? 1 : jsonCases.Count(item => item.JsonValid) / (double)jsonCases.Length;
        var averageLatency = results.Count == 0 ? 0 : (long)results.Average(item => item.DurationMilliseconds);
        return new ModelBenchmarkReport(
            modelId,
            _clock.UtcNow,
            results,
            successRate,
            jsonRate,
            averageLatency);
    }

    private static bool IsValidJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // Match production report parsing: tolerate code fences or prose around the JSON object.
        var start = text.IndexOf('{', StringComparison.Ordinal);
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return false;
        }

        try
        {
            using var _ = JsonDocument.Parse(text[start..(end + 1)]);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
