using System.Globalization;
using System.Text.Json;
using AiAssistant.Application.Ports;
using AiAssistant.Application.UseCases;
using AiAssistant.Core.Domain;

namespace AiAssistant.Voice;

public enum VoiceCommandKind
{
    Unknown = 0,
    GenerateReport = 1,
    PauseCapture = 2,
    ResumeCapture = 3,
    OpenTimeline = 4,
    OpenReports = 5,
    ExitApplication = 6,
    Confirm = 7,
    Cancel = 8,
    AskQuestion = 9
}

public sealed record ParsedVoiceCommand(
    VoiceCommandKind Kind,
    string Transcript,
    string Feedback,
    bool RequiresConfirmation,
    string ParamsJson = "{}",
    string? Question = null);

internal sealed class VoiceCommandParser
{
    public static ParsedVoiceCommand Parse(string? transcript, IClock? clock = null)
    {
        var raw = transcript?.Trim() ?? string.Empty;
        var normalized = new string(raw.Where(ch => !char.IsWhiteSpace(ch) && !char.IsPunctuation(ch)).ToArray());
        if (string.IsNullOrEmpty(normalized))
        {
            return Unknown(raw, "没有听清，请再说一次。");
        }

        if (normalized is "确认" or "确定" or "是的" or "执行")
        {
            return new ParsedVoiceCommand(VoiceCommandKind.Confirm, raw, "已确认。", false);
        }

        if (normalized is "取消" or "不用了" or "算了" or "停止")
        {
            return new ParsedVoiceCommand(VoiceCommandKind.Cancel, raw, "已取消。", false);
        }

        if ((normalized.Contains("打开", StringComparison.Ordinal) || normalized.Contains("查看", StringComparison.Ordinal)) &&
            normalized.Contains("日报", StringComparison.Ordinal))
        {
            return new ParsedVoiceCommand(VoiceCommandKind.OpenReports, raw, "正在打开日报。", false);
        }

        if ((normalized.Contains("打开", StringComparison.Ordinal) || normalized.Contains("查看", StringComparison.Ordinal)) &&
            normalized.Contains("时间线", StringComparison.Ordinal))
        {
            return new ParsedVoiceCommand(VoiceCommandKind.OpenTimeline, raw, "正在打开时间线。", false);
        }

        if (normalized.Contains("生成", StringComparison.Ordinal) &&
            normalized.Contains("日报", StringComparison.Ordinal))
        {
            return new ParsedVoiceCommand(
                VoiceCommandKind.GenerateReport,
                raw,
                "正在生成今天的日报。",
                false,
                JsonSerializer.Serialize(new
                {
                    date = DateOnly
                        .FromDateTime((clock ?? new SystemClock()).LocalNow.LocalDateTime.Date)
                        .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                }));
        }

        if (normalized.Contains("暂停", StringComparison.Ordinal) &&
            (normalized.Contains("采集", StringComparison.Ordinal) || normalized.Contains("记录", StringComparison.Ordinal)))
        {
            return new ParsedVoiceCommand(VoiceCommandKind.PauseCapture, raw, "已暂停采集。", false);
        }

        if ((normalized.Contains("恢复", StringComparison.Ordinal) || normalized.Contains("继续", StringComparison.Ordinal)) &&
            (normalized.Contains("采集", StringComparison.Ordinal) || normalized.Contains("记录", StringComparison.Ordinal)))
        {
            return new ParsedVoiceCommand(VoiceCommandKind.ResumeCapture, raw, "已恢复采集。", false);
        }

        if (normalized.Contains("退出", StringComparison.Ordinal) ||
            normalized.Contains("关闭应用", StringComparison.Ordinal))
        {
            return new ParsedVoiceCommand(
                VoiceCommandKind.ExitApplication,
                raw,
                "退出应用需要再次确认，请说确认或取消。",
                true);
        }

        var question = ExtractQuestion(normalized);
        if (question is not null)
        {
            return new ParsedVoiceCommand(
                VoiceCommandKind.AskQuestion,
                raw,
                "正在检索本地证据。",
                false,
                JsonSerializer.Serialize(new { question }),
                question);
        }

        return Unknown(raw, "暂不支持该命令。可说：生成日报、暂停采集、恢复采集、打开时间线、打开日报，或以“问一下”开头提问。");
    }

    private static readonly string[] QuestionPrefixes = ["问一下", "请问", "查询", "搜索", "问"];

    /// <summary>Only fixed trigger phrases open free-form Q&amp;A, so the wake window never becomes always-on recording.</summary>
    private static string? ExtractQuestion(string normalized)
    {
        foreach (var prefix in QuestionPrefixes)
        {
            if (!normalized.StartsWith(prefix, StringComparison.Ordinal) || normalized.Length <= prefix.Length + 1)
            {
                continue;
            }

            var question = normalized[prefix.Length..];
            return question.Length >= 2 ? question : null;
        }

        return null;
    }

    private static ParsedVoiceCommand Unknown(string transcript, string feedback) =>
        new(VoiceCommandKind.Unknown, transcript, feedback, false);
}

public sealed record VoiceCommandOutcome(bool Handled, string Feedback);

public sealed class VoiceCommandRouter(
    ReportUseCase reportUseCase,
    CaptureControlUseCase captureControl,
    IVoiceViewNavigator navigator,
    IClock clock,
    EvidenceSearchUseCase? evidence = null)
{
    /// <summary>
    /// Local models can need far longer than the voice command window. Bounding the Q&amp;A call
    /// below that window guarantees a spoken answer instead of the generic "command timed out"
    /// path, which used to leave the session silent and stranded.
    /// </summary>
    public TimeSpan QuestionTimeout { get; init; } = TimeSpan.FromSeconds(20);

    public async Task<VoiceCommandOutcome> ExecuteAsync(ParsedVoiceCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        switch (command.Kind)
        {
            case VoiceCommandKind.GenerateReport:
                _ = await reportUseCase.RequestAsync(
                    clock.Now().LocalDate,
                    forceOverwrite: false,
                    ct);
                return new VoiceCommandOutcome(true, "日报任务已排队，完成后可在日报页查看。");
            case VoiceCommandKind.PauseCapture:
                await captureControl.PauseAsync("语音命令", ct);
                return new VoiceCommandOutcome(true, "已暂停采集。");
            case VoiceCommandKind.ResumeCapture:
                await captureControl.ResumeAsync(ct);
                return new VoiceCommandOutcome(true, "已恢复采集。");
            case VoiceCommandKind.OpenTimeline:
                navigator.ShowTimeline();
                return new VoiceCommandOutcome(true, "已打开时间线。");
            case VoiceCommandKind.OpenReports:
                navigator.ShowReports();
                return new VoiceCommandOutcome(true, "已打开日报。");
            case VoiceCommandKind.ExitApplication:
                navigator.RequestExit();
                return new VoiceCommandOutcome(true, "正在退出应用。");
            case VoiceCommandKind.AskQuestion:
                return await AnswerQuestionAsync(command, ct);
            default:
                return new VoiceCommandOutcome(false, command.Feedback);
        }
    }

    /// <summary>Voice Q&amp;A reuses the exact same evidence use case as the search panel; no separate answer logic.</summary>
    private async Task<VoiceCommandOutcome> AnswerQuestionAsync(ParsedVoiceCommand command, CancellationToken ct)
    {
        if (evidence is null)
        {
            return new VoiceCommandOutcome(false, "问答暂不可用，请打开检索面板。");
        }

        var question = command.Question ?? command.Transcript;
        if (string.IsNullOrWhiteSpace(question))
        {
            return new VoiceCommandOutcome(false, "没有听清问题，请再说一次。");
        }

        using var questionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        questionCts.CancelAfter(QuestionTimeout);
        EvidenceQaAnswer answer;
        try
        {
            answer = await evidence.AskRecentAsync(question, days: 7, questionCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The local model spent the whole voice budget. The retrieved evidence is still
            // verifiable, so speak the matching records instead of "timed out, say it again".
            return await AnswerFromEvidenceAsync(question, ct);
        }

        if (!answer.HasEvidence)
        {
            return new VoiceCommandOutcome(true, "没有找到可验证的本地证据，暂不回答。");
        }

        var text = answer.Answer.Replace('\r', ' ').Replace('\n', ' ').Trim();
        // Spoken answers stay short on purpose: every character costs synthesis plus playback time
        // inside the command window, and the full answer is still visible in the search panel.
        var feedback = text.Length <= 50 ? text : text[..50] + "…";
        return new VoiceCommandOutcome(true, feedback);
    }

    /// <summary>
    /// Model-free fallback for the voice budget. Reading back the top matches keeps the command
    /// useful when a large local model is still thinking after the Q&amp;A window closes.
    /// </summary>
    private async Task<VoiceCommandOutcome> AnswerFromEvidenceAsync(string question, CancellationToken ct)
    {
        if (evidence is null)
        {
            return new VoiceCommandOutcome(false, "本地模型响应超时，请稍后再问。");
        }

        try
        {
            var (from, to) = evidence.DefaultRange(7);
            var result = await evidence.SearchAsync(question, from, to, ct);
            if (result.Evidence.Count == 0)
            {
                return new VoiceCommandOutcome(true, "本地模型响应超时，也没有找到可验证的本地记录。");
            }

            var top = result.Evidence
                .Take(2)
                .Select(item => $"{item.AppName}：{item.Title}");
            return new VoiceCommandOutcome(true, $"模型响应超时。相关记录：{string.Join("；", top)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new VoiceCommandOutcome(true, "本地模型响应超时，请稍后再问，或打开检索面板查看证据。");
        }
    }
}
