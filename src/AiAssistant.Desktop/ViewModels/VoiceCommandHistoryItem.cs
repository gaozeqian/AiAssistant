using System.Globalization;
using AiAssistant.Application.Ports;

namespace AiAssistant.Desktop.ViewModels;

/// <summary>One persisted voice command shown on the voice page, with its replay affordance.</summary>
public sealed class VoiceCommandHistoryItem(VoiceCommandLogRecord record)
{
    public VoiceCommandLogRecord Record { get; } = record;

    public string TimeText => Record.OccurredAtUtc.ToLocalTime()
        .ToString("MM-dd HH:mm:ss", CultureInfo.CurrentCulture);

    public string Transcript => string.IsNullOrWhiteSpace(Record.Transcript)
        ? "（未识别到文本）"
        : Record.Transcript;

    public string IntentText => Record.Intent switch
    {
        "GenerateReport" => "生成日报",
        "PauseCapture" => "暂停采集",
        "ResumeCapture" => "恢复采集",
        "OpenTimeline" => "打开时间线",
        "OpenReports" => "打开日报",
        "ExitApplication" => "退出应用",
        "AskQuestion" => "提问",
        "Confirm" => "确认",
        "Cancel" => "取消",
        "Unknown" => "未识别",
        _ => Record.Intent
    };

    public bool HasAudio => !string.IsNullOrWhiteSpace(Record.AudioContentId);

    public string AudioStateText => HasAudio ? "已留存" : "未留存";
}
