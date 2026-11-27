using System.Globalization;
using AiAssistant.Application.Ports;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiAssistant.Desktop.ViewModels;

/// <summary>One persisted voice command shown on the voice page, with its replay affordance.</summary>
public sealed class VoiceCommandHistoryItem(VoiceCommandLogRecord record) : ObservableObject
{
    private bool _isPlaying;
    private bool _audioCleared;

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

    /// <summary>Only the row that is actually playing is disabled; the rest stay replayable.</summary>
    public bool IsPlaying
    {
        get => _isPlaying;
        internal set
        {
            if (SetProperty(ref _isPlaying, value))
            {
                OnPropertyChanged(nameof(ReplayLabel));
                OnPropertyChanged(nameof(CanReplay));
            }
        }
    }

    public bool HasAudio => !_audioCleared && !string.IsNullOrWhiteSpace(Record.AudioContentId);

    public bool CanReplay => HasAudio && !IsPlaying;

    public string ReplayLabel => IsPlaying ? "播放中" : "重听";

    /// <summary>Distinguishes "never recorded" from "recorded, then erased by the retention sweep".</summary>
    public string AudioStateText => HasAudio ? "已留存" : WasAudioCleared ? "已清理" : "未留存";

    /// <summary>Drives the warning tone of <see cref="AudioStateText"/> in the list.</summary>
    public bool WasAudioCleared => _audioCleared || Record.AudioClearedAtUtc is not null;

    public string AudioStateHint => HasAudio
        ? "音频已加密留存，可重听"
        : Record.AudioClearedAtUtc is { } clearedAt
            ? $"音频已于 {clearedAt.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.CurrentCulture)} 按保留期清理"
            : _audioCleared
                ? "音频已被保留期清理"
                : "该命令未记录音频";

    /// <summary>The blob disappeared after the list was loaded; keep the row honest instead of
    /// offering a replay that can only fail.</summary>
    internal void MarkAudioCleared()
    {
        if (!HasAudio)
        {
            return;
        }

        _audioCleared = true;
        OnPropertyChanged(nameof(HasAudio));
        OnPropertyChanged(nameof(CanReplay));
        OnPropertyChanged(nameof(AudioStateText));
        OnPropertyChanged(nameof(AudioStateHint));
        OnPropertyChanged(nameof(WasAudioCleared));
    }
}
