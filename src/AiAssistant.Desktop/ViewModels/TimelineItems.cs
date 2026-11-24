using System.Globalization;
using AiAssistant.Core.Domain;

namespace AiAssistant.Desktop.ViewModels;

/// <summary>Stateless display projection of a timeline date.</summary>
public sealed record TimelineDateItem(DateOnly Date)
{
    // The panel is Chinese-only: format the weekday with zh-CN so an English OS locale does not
    // render "2026年11月22日 Sunday" in the date picker.
    private static readonly CultureInfo DisplayCulture = CultureInfo.GetCultureInfo("zh-CN");

    public string DisplayName => Date.ToString("yyyy年MM月dd日 dddd", DisplayCulture);
}

/// <summary>Stateless display projection of one activity event.</summary>
public sealed class TimelineEventItem(ActivityEvent activityEvent)
{
    public ActivityEvent Event { get; } = activityEvent ?? throw new ArgumentNullException(nameof(activityEvent));

    public string TimeText => Event.Timestamp.ToCapturedLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);

    public string AppName => Event.AppName;

    public string Title => string.IsNullOrWhiteSpace(Event.WindowTitle) ? Event.AppName : Event.WindowTitle;

    public string SourceText => Event.Source switch
    {
        SourceKind.WindowActivity => "窗口活动",
        SourceKind.ScreenFrame => "屏幕快照",
        SourceKind.FileActivity => "文件活动",
        SourceKind.MicrophoneAudio => "麦克风",
        SourceKind.BrowserTab => "浏览器",
        SourceKind.SystemControl => "控制记录",
        _ => "其他"
    };

    public string? DetailText => Event.Summary ?? Event.ExtractedText;

    public bool HasDetailText => !string.IsNullOrWhiteSpace(DetailText);

    public bool HasMedia => Event.MediaRef is not null;

    /// <summary>Which monitor the frame came from, when the capture adapter recorded one.</summary>
    public string? DisplayText =>
        Event.Metadata.TryGetValue(Application.Ports.ScreenCaptureMetadata.DisplayName, out var name) &&
        !string.IsNullOrWhiteSpace(name)
            ? $"显示器 {name}"
            : null;

    public bool HasDisplayText => DisplayText is not null;

    public string CapturedTimeText => Event.Timestamp.ToCapturedLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.CurrentCulture);

    public string MediaSizeText => Event.MediaRef is null ? string.Empty : $"{Event.MediaRef.PlaintextLength / 1024d:F1} KB · 仅在选中时解密";
}
