using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AiAssistant.Application.Ports;
using AiAssistant.Application.UseCases;
using AiAssistant.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiAssistant.Desktop.ViewModels;

public sealed class ReportsViewModel : ObservableObject, IDisposable
{
    private readonly ReportUseCase _reports;
    private readonly RangeReportUseCase? _range;
    private readonly ReportScheduleSettingsUseCase? _scheduleSettings;
    private readonly string _exportDirectory;
    private readonly IClock _clock;
    private DateTimeOffset? _selectedDate;
    private ReportVersionItem? _selectedVersion;
    private string _markdown = string.Empty;
    private string _standupText = "尚无站会数据";
    private string _statusMessage = "选择日期后可生成或查看日报";
    private string _errorMessage = string.Empty;
    private TimeSpan? _scheduleTime;
    private bool _scheduleEnabled = true;
    private string _scheduleStatusMessage = string.Empty;
    private bool _isScheduleSaving;
    private bool _isBusy;
    private bool _isWeeklyView;
    private RangeReport? _weeklyReport;
    private string _weeklyStatus = "尚未生成周报";
    private string _weeklyMarkdown = string.Empty;
    private double _progressPulseValue;
    private readonly ProgressPulseController _progressPulse;

    public ReportsViewModel(
        ReportUseCase reports,
        string exportDirectory,
        IClock? clock = null,
        ReportScheduleSettingsUseCase? scheduleSettings = null,
        RangeReportUseCase? range = null)
    {
        _reports = reports ?? throw new ArgumentNullException(nameof(reports));
        _range = range;
        _exportDirectory = exportDirectory ?? throw new ArgumentNullException(nameof(exportDirectory));
        _clock = clock ?? new SystemClock();
        _scheduleSettings = scheduleSettings;
        _progressPulse = new ProgressPulseController(value => ProgressPulse = value);
        _selectedDate = _clock.LocalNow.Date;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        GenerateCommand = new AsyncRelayCommand(() => GenerateAsync(forceOverwrite: false));
        RegenerateCommand = new AsyncRelayCommand(() => GenerateAsync(forceOverwrite: true));
        ExportCommand = new AsyncRelayCommand(ExportAsync);
        SaveScheduleCommand = new AsyncRelayCommand(SaveScheduleAsync);
        WeeklyReportCommand = new AsyncRelayCommand(GenerateWeeklyAsync, () => _range is not null);
        ExportWeeklyCommand = new AsyncRelayCommand(ExportWeeklyAsync, () => _weeklyReport is not null);
        ShowDailyCommand = new RelayCommand(() => IsWeeklyView = false);
        ShowWeeklyCommand = new RelayCommand(() => IsWeeklyView = true);
        Versions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasVersions));
    }

    public ObservableCollection<ReportVersionItem> Versions { get; } = [];

    /// <summary>True when the selected date already has at least one report version.</summary>
    public bool HasVersions => Versions.Count > 0;

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand GenerateCommand { get; }
    public IAsyncRelayCommand RegenerateCommand { get; }
    public IAsyncRelayCommand ExportCommand { get; }
    public IAsyncRelayCommand SaveScheduleCommand { get; }

    /// <summary>Generates the weekly range report for the week containing the selected date.</summary>
    public IAsyncRelayCommand WeeklyReportCommand { get; }

    public IAsyncRelayCommand ExportWeeklyCommand { get; }

    public IRelayCommand ShowDailyCommand { get; }

    public IRelayCommand ShowWeeklyCommand { get; }

    public ObservableCollection<DailyBarItem> WeeklyBars { get; } = [];

    /// <summary>日报/周报共用右侧阅读区，用分段按钮切换。</summary>
    public bool IsWeeklyView
    {
        get => _isWeeklyView;
        private set
        {
            if (SetProperty(ref _isWeeklyView, value))
            {
                OnPropertyChanged(nameof(ShowDailyView));
                OnPropertyChanged(nameof(ContentTitle));
            }
        }
    }

    public bool ShowDailyView => !IsWeeklyView;

    public string ContentTitle => IsWeeklyView ? "周报正文" : "日报正文";

    public string WeeklyStatus
    {
        get => _weeklyStatus;
        private set => SetProperty(ref _weeklyStatus, value);
    }

    public string WeeklyMarkdown
    {
        get => _weeklyMarkdown;
        private set
        {
            if (SetProperty(ref _weeklyMarkdown, value))
            {
                OnPropertyChanged(nameof(HasWeeklyMarkdown));
            }
        }
    }

    public bool HasWeeklyMarkdown => !string.IsNullOrEmpty(WeeklyMarkdown);

    public bool HasWeeklyReport => _weeklyReport is not null;

    public bool HasScheduleSettings => _scheduleSettings is not null;

    /// <summary>Local wall-clock time for the automatic previous-day check.</summary>
    public TimeSpan? ScheduleTime
    {
        get => _scheduleTime;
        set => SetProperty(ref _scheduleTime, value);
    }

    public bool ScheduleEnabled
    {
        get => _scheduleEnabled;
        set => SetProperty(ref _scheduleEnabled, value);
    }

    public bool IsScheduleSaving
    {
        get => _isScheduleSaving;
        private set => SetProperty(ref _isScheduleSaving, value);
    }

    public string ScheduleStatusMessage
    {
        get => _scheduleStatusMessage;
        private set => SetProperty(ref _scheduleStatusMessage, value);
    }

    public DateTimeOffset? SelectedDate
    {
        get => _selectedDate;
        set
        {
            if (SetProperty(ref _selectedDate, value))
            {
                _ = RefreshAsync();
            }
        }
    }

    public ReportVersionItem? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (SetProperty(ref _selectedVersion, value))
            {
                Markdown = value?.Report.Markdown ?? string.Empty;
                StandupText = value is null ? "尚无站会数据" : FormatStandup(value.Report.StatsJson);
                OnPropertyChanged(nameof(HasReport));
                OnPropertyChanged(nameof(VersionText));
            }
        }
    }

    public string Markdown
    {
        get => _markdown;
        private set
        {
            if (SetProperty(ref _markdown, value))
            {
                OnPropertyChanged(nameof(HasMarkdown));
            }
        }
    }

    /// <summary>True when the body has text; the empty-state overlay keys off the body, not the selection.</summary>
    public bool HasMarkdown => !string.IsNullOrEmpty(Markdown);

    public string StandupText
    {
        get => _standupText;
        private set => SetProperty(ref _standupText, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                _progressPulse.SetBusy(value);
            }
        }
    }

    /// <summary>Determinate 0..100 progress driven while busy, so it keeps advancing across tab switches.</summary>
    public double ProgressPulse
    {
        get => _progressPulseValue;
        private set => SetProperty(ref _progressPulseValue, value);
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool HasReport => SelectedVersion is not null;

    /// <summary>Save-file dialog, wired by the shell after the window exists; null falls back to the export directory.</summary>
    public ISaveFilePicker? SaveFilePicker { get; set; }

    public string VersionText => SelectedVersion is null
        ? string.Empty
        : $"v{SelectedVersion.Report.Version} · {SelectedVersion.Report.ModelId} · {SelectedVersion.Report.GeneratedAtUtc.ToLocalTime():MM-dd HH:mm}";

    public async Task InitializeAsync()
    {
        LoadScheduleSettings();
        await RefreshAsync();
    }

    private void LoadScheduleSettings()
    {
        if (_scheduleSettings is null)
        {
            return;
        }

        try
        {
            var settings = _scheduleSettings.Get();
            ScheduleTime = settings.DailyLocalTime.ToTimeSpan();
            ScheduleEnabled = settings.Enabled;
            ScheduleStatusMessage = settings.Enabled
                ? "已启用：" + DescribeSchedule(settings)
                : DescribeSchedule(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ScheduleStatusMessage = "读取自动日报设置失败。";
        }
    }

    private async Task SaveScheduleAsync()
    {
        if (_scheduleSettings is null || IsScheduleSaving)
        {
            return;
        }

        if (ScheduleTime is null)
        {
            ScheduleStatusMessage = "请选择自动检查时间。";
            return;
        }

        IsScheduleSaving = true;
        try
        {
            var time = TimeOnly.FromTimeSpan(ScheduleTime.Value);
            await _scheduleSettings.SaveAsync(time, ScheduleEnabled);
            ScheduleStatusMessage = "已保存。" + DescribeSchedule(new ReportScheduleSettings(time, ScheduleEnabled));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            ScheduleStatusMessage = "保存自动日报设置失败，请检查数据目录权限。";
        }
        finally
        {
            IsScheduleSaving = false;
        }
    }

    private static string DescribeSchedule(ReportScheduleSettings settings) =>
        settings.Enabled
            ? $"每天 {settings.DailyLocalTime.ToString("HH:mm", CultureInfo.InvariantCulture)} 检查前一天日报"
            : "自动检查已关闭；仍可手动生成日报";

    private async Task RefreshAsync()
    {
        if (IsBusy || SelectedDate is null)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            var date = DateOnly.FromDateTime(SelectedDate.Value.Date);
            await LoadVersionsAsync(date);
            StatusMessage = Versions.Count == 0
                ? $"{date:yyyy-MM-dd} 还没有日报"
                : $"已加载 {Versions.Count} 个日报版本";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Core.Exceptions.AiAssistantDomainException)
        {
            ErrorMessage = "读取日报失败，请检查加密数据库状态。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadVersionsAsync(DateOnly date)
    {
        var versions = await _reports.ListVersionsAsync(date);
        Versions.Clear();
        foreach (var report in versions)
        {
            Versions.Add(new ReportVersionItem(report));
        }

        SelectedVersion = Versions.FirstOrDefault();
    }

    private async Task GenerateAsync(bool forceOverwrite)
    {
        if (IsBusy || SelectedDate is null)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        StatusMessage = forceOverwrite ? "正在强制重新生成日报…" : "正在生成日报…";
        try
        {
            var date = DateOnly.FromDateTime(SelectedDate.Value.Date);
            var report = await _reports.RequestAndWaitAsync(date, forceOverwrite);
            if (report is not null)
            {
                await LoadVersionsAsync(date);
                SelectedVersion = Versions.FirstOrDefault(item => item.Report.Id == report.Id) ?? Versions.FirstOrDefault();
                StatusMessage = $"日报 v{report.Version} 已生成";
            }
            else
            {
                StatusMessage = "任务已排队，稍后刷新查看结果";
            }
        }
        catch (TimeoutException)
        {
            ErrorMessage = "日报任务仍在运行，请稍后刷新。";
        }
        catch (Exception ex)
        {
            // Show the underlying reason (model endpoint, structured-output rejection, policy, ...).
            ErrorMessage = $"日报生成失败：{ex.Message}（原始活动数据未被修改）";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExportAsync()
    {
        if (SelectedVersion is null || IsBusy)
        {
            return;
        }

        try
        {
            var report = SelectedVersion.Report;
            string path;
            string successMessage;
            if (SaveFilePicker is not null)
            {
                var types = new List<SaveFileType>
                {
                    new("Markdown", "md"),
                    new("SVG 图表", "svg"),
                    new("PNG 图片", "png"),
                    new("PDF 文档", "pdf"),
                    new("JSON", "json")
                };
                var picked = await SaveFilePicker.PickSaveFileAsync(
                    $"AiAssistant-日报-{report.LocalDate:yyyyMMdd}-v{report.Version}.md",
                    types);
                if (picked is null)
                {
                    return;
                }

                path = await _reports.ExportSingleAsync(
                    report,
                    ReportExportFormatExtensions.FromExtension(picked.Extension),
                    picked.Path);
                successMessage = $"已导出：{path}";
            }
            else
            {
                var export = await _reports.ExportAsync(report, _exportDirectory);
                path = export.MarkdownPath;
                successMessage = export.PngPath is null
                    ? $"已导出日报（Markdown / SVG / JSON）：{path}"
                    : $"已导出日报（Markdown / SVG / PNG / PDF / JSON）：{path}";
            }

            StatusMessage = successMessage;
            ErrorMessage = string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ErrorMessage = "导出失败，请检查目标目录权限。";
        }
    }

    /// <summary>
    /// 周报以"选中日期所在周"为锚点：和日报共用同一个日期选择器，避免再引入一个日期输入。
    /// </summary>
    private async Task GenerateWeeklyAsync()
    {
        if (_range is null || IsBusy || SelectedDate is null)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        WeeklyStatus = "正在生成周报…";
        try
        {
            var anchor = DateOnly.FromDateTime(SelectedDate.Value.Date);
            var report = await _range.GenerateWeekAsync(anchor, CancellationToken.None);
            _weeklyReport = report;
            WeeklyMarkdown = report.Markdown;
            WeeklyBars.Clear();
            foreach (var day in report.DailyStats)
            {
                WeeklyBars.Add(new DailyBarItem(day, report.MaxDailyEvents));
            }

            WeeklyStatus = string.Create(
                CultureInfo.InvariantCulture,
                $"{report.FromDate:MM-dd} ~ {report.ToDate:MM-dd} · {report.TotalEvents} 条 · 活跃 {report.TotalActiveMinutes} 分钟 · {(report.Degraded ? $"统计模式（{report.DegradedReason}）" : "本地模型摘要")}");
            OnPropertyChanged(nameof(HasWeeklyReport));
            ExportWeeklyCommand.NotifyCanExecuteChanged();
            IsWeeklyView = true;
        }
        catch (Exception ex)
        {
            WeeklyStatus = "周报生成失败";
            ErrorMessage = $"周报生成失败：{ex.Message}（原始活动数据未被修改）";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExportWeeklyAsync()
    {
        if (_range is null || _weeklyReport is null || IsBusy)
        {
            return;
        }

        try
        {
            var report = _weeklyReport;
            string path;
            string successMessage;
            if (SaveFilePicker is not null)
            {
                var types = new List<SaveFileType>
                {
                    new("Markdown", "md"),
                    new("SVG 图表", "svg"),
                    new("PNG 图片", "png"),
                    new("PDF 文档", "pdf"),
                    new("JSON", "json")
                };
                var picked = await SaveFilePicker.PickSaveFileAsync(
                    $"AiAssistant-周报-{report.FromDate:yyyyMMdd}-{report.ToDate:yyyyMMdd}.md",
                    types);
                if (picked is null)
                {
                    return;
                }

                path = await _range.ExportSingleAsync(
                    report,
                    ReportExportFormatExtensions.FromExtension(picked.Extension),
                    picked.Path,
                    CancellationToken.None);
                successMessage = $"已导出周报：{path}";
            }
            else
            {
                var export = await _range.ExportAsync(report, _exportDirectory, CancellationToken.None);
                path = export.MarkdownPath;
                successMessage = export.PngPath is null
                    ? $"已导出周报（Markdown / SVG / JSON）：{path}"
                    : $"已导出周报（Markdown / SVG / PNG / PDF / JSON）：{path}";
            }

            WeeklyStatus = successMessage;
            ErrorMessage = string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ErrorMessage = "周报导出失败，请检查目标目录权限。";
        }
    }

    /// <summary>
    /// Reads the standup block out of the persisted stats JSON. The model-facing schema uses
    /// lowercase field names (yesterday/today/blockers) while older hand-written fixtures use
    /// PascalCase, so every lookup is case-insensitive.
    /// </summary>
    private static string FormatStandup(string statsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(statsJson);
            if (!TryGetProperty(document.RootElement, "Standup", out var standup))
            {
                return "该版本没有站会内容";
            }

            static string ReadString(JsonElement element, string property) =>
                TryGetProperty(element, property, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? string.Empty
                    : string.Empty;

            var yesterday = ReadString(standup, "Yesterday");
            var today = ReadString(standup, "Today");
            var blockers = TryGetProperty(standup, "Blockers", out var blockerArray) &&
                           blockerArray.ValueKind == JsonValueKind.Array
                ? string.Join(
                    "；",
                    blockerArray.EnumerateArray()
                        .Where(item => item.ValueKind == JsonValueKind.String)
                        .Select(item => item.GetString())
                        .Where(item => !string.IsNullOrWhiteSpace(item)))
                : string.Empty;

            if (string.IsNullOrWhiteSpace(yesterday) &&
                string.IsNullOrWhiteSpace(today) &&
                string.IsNullOrWhiteSpace(blockers))
            {
                return "该版本没有站会内容";
            }

            var builder = new StringBuilder();
            builder.AppendLine(CultureInfo.InvariantCulture, $"昨天：{(string.IsNullOrWhiteSpace(yesterday) ? "未记录" : yesterday)}");
            builder.AppendLine(CultureInfo.InvariantCulture, $"今天：{(string.IsNullOrWhiteSpace(today) ? "未记录" : today)}");
            builder.Append(CultureInfo.InvariantCulture, $"阻塞：{(string.IsNullOrWhiteSpace(blockers) ? "无" : blockers)}");
            return builder.ToString();
        }
        catch (JsonException)
        {
            return "站会数据格式不可用";
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    public void Dispose() => _progressPulse.Dispose();
}

public sealed class ReportVersionItem
{
    public ReportVersionItem(DailyReport report)
    {
        Report = report;
    }

    public DailyReport Report { get; }

    public string DisplayText => $"v{Report.Version} · {Report.GeneratedAtUtc.ToLocalTime():MM-dd HH:mm} · {Report.TriggerSource switch
    {
        ReportTriggerSource.Manual => "手动",
        ReportTriggerSource.StartupCatchUp => "启动补偿",
        ReportTriggerSource.ScheduledDaily => "定时",
        ReportTriggerSource.VoiceCommand => "语音",
        _ => "其他"
    }}";
}
