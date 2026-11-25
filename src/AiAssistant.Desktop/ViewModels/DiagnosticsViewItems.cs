using System.Globalization;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;
using CommunityToolkit.Mvvm.Input;

namespace AiAssistant.Desktop.ViewModels;

public sealed class CapabilityItem(CapabilityState capability, IRelayCommand? openSettingsCommand = null)
{
    public string KindText => KindLabel(capability.Kind);

    public static string KindLabel(CapabilityKind kind) => kind switch
    {
        CapabilityKind.ScreenCapture => "屏幕采集",
        CapabilityKind.WindowActivity => "窗口活动",
        CapabilityKind.FileActivity => "文件活动",
        CapabilityKind.AudioCapture => "音频采集",
        CapabilityKind.AccessibilityText => "无障碍文本",
        CapabilityKind.Ocr => "OCR",
        CapabilityKind.GlobalHotkey => "全局热键",
        CapabilityKind.SystemTray => "系统托盘",
        CapabilityKind.AutoStart => "开机自启",
        CapabilityKind.KeyStore => "系统密钥库",
        CapabilityKind.VectorSearch => "向量检索",
        CapabilityKind.WakeWord => "唤醒词",
        CapabilityKind.ModelGateway => "本机模型",
        _ => kind.ToString()
    };

    public string StatusText => capability.Status switch
    {
        CapabilityStatus.Supported => "可用",
        CapabilityStatus.PermissionRequired => "需要权限",
        CapabilityStatus.Degraded => "降级",
        CapabilityStatus.Unavailable => "不可用",
        CapabilityStatus.DisabledByPolicy => "策略禁用",
        _ => "未知"
    };

    public string Summary => capability.Summary;

    public string RemediationHint => capability.RemediationHint ?? "无需额外操作";

    public bool NeedsAttention => capability.Status is not CapabilityStatus.Supported;

    /// <summary>Whether an actionable button should be offered (permission / broken / reduced capability).</summary>
    public bool NeedsAction => capability.Status is CapabilityStatus.PermissionRequired or CapabilityStatus.Unavailable or CapabilityStatus.Degraded;

    public string ActionLabel => capability.Status == CapabilityStatus.PermissionRequired ? "去授权" : "打开系统设置";

    public IRelayCommand? OpenSettingsCommand { get; } = openSettingsCommand;
}

public sealed class ReportJobItem(ReportJob job)
{
    public string DateText => job.TargetDate.ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);

    public string StatusText => job.Status switch
    {
        ReportJobStatus.Pending => "等待执行",
        ReportJobStatus.Running => "执行中",
        ReportJobStatus.Completed => "已完成",
        ReportJobStatus.SkippedAlreadyExists => "已跳过",
        ReportJobStatus.Failed => "失败",
        _ => "未知"
    };

    public string AttemptText => $"第 {job.Attempts} 次";

    public string CreatedText => job.CreatedAtUtc.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.CurrentCulture);
}

public sealed class DiagnosticLogItem(DiagnosticLogEntry entry)
{
    public string TimeText => entry.TimestampUtc.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
    public string LevelText => entry.Level switch
    {
        "Information" or "Info" => "信息",
        "Warning" or "Warn" => "警告",
        "Error" => "错误",
        "Debug" => "调试",
        "Trace" => "跟踪",
        _ => entry.Level
    };
    public string CategoryText => entry.Category;
    public string MessageText => entry.SanitizedMessage;
}

public sealed record ModelTierOption(string Value, string Label);

public sealed record ModelThinkingLevelOption(string Value, string Label);
