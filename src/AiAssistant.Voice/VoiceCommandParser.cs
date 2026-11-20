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
    Cancel = 8
}

public sealed record ParsedVoiceCommand(
    VoiceCommandKind Kind,
    string Transcript,
    string Feedback,
    bool RequiresConfirmation,
    string ParamsJson = "{}");

public sealed class VoiceCommandParser
{
    public static ParsedVoiceCommand Parse(string? transcript)
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
                JsonSerializer.Serialize(new { date = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }));
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

        return Unknown(raw, "暂不支持该命令。可说：生成日报、暂停采集、恢复采集、打开时间线或打开日报。");
    }

    private static ParsedVoiceCommand Unknown(string transcript, string feedback) =>
        new(VoiceCommandKind.Unknown, transcript, feedback, false);
}

public sealed record VoiceCommandOutcome(bool Handled, string Feedback);

public sealed class VoiceCommandRouter(
    ReportUseCase reportUseCase,
    CaptureControlUseCase captureControl,
    IVoiceViewNavigator navigator,
    IClock clock)
{
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
            default:
                return new VoiceCommandOutcome(false, command.Feedback);
        }
    }
}
