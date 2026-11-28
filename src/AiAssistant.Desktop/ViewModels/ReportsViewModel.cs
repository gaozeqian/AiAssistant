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
    private readonly WeeklyReportUseCase? _weekly;
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
    private WeeklyReport? _selectedWeekly;
    private WeeklyVersionItem? _selectedWeeklyVersion;
    private string _weeklyStatus = "尚未生成周报";
    private string _weeklyMarkdown = string.Empty;
    private bool _isDailyScheduleExpanded;
    private bool _isWeeklyScheduleExpanded;
    private bool _weeklyScheduleEnabled;
    private WeekdayOption _weeklyScheduleDay = WeekdayOption.All[6];
    private TimeSpan? _weeklyScheduleTime;
    private string _weeklyScheduleStatus = string.Empty;
    private double _progressPulseValue;
    private readonly ProgressPulseController _progressPulse;

    public ReportsViewModel(
        ReportUseCase reports,
        string exportDirectory,
        IClock? clock = null,
        ReportScheduleSettingsUseCase? scheduleSettings = null,
        RangeReportUseCase? range = null,
        WeeklyReportUseCase? weekly = null)
    {
        _reports = reports ?? throw new ArgumentNullException(nameof(reports));
        _range = range;
        _weekly = weekly;
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
        WeeklyReportCommand = new AsyncRelayCommand(() => GenerateWeeklyAsync(force: false), () => _weekly is not null);
        WeeklyRegenerateCommand = new AsyncRelayCommand(() => GenerateWeeklyAsync(force: true), () => _weekly is not null);
        WeeklyRefreshCommand = new AsyncRelayCommand(RefreshWeeklyAsync, () => _weekly is not null);
        ExportWeeklyCommand = new AsyncRelayCommand(ExportWeeklyAsync, () => _selectedWeekly is not null);
        SaveWeeklyScheduleCommand = new AsyncRelayCommand(SaveWeeklyScheduleAsync);
        // 左栏只放一行「生成 / 强制重生成 / 刷新 / 导出」，由当前视图决定落到日报还是周报。
        ActiveGenerateCommand = new AsyncRelayCommand(
            async () => await (IsWeeklyView ? WeeklyReportCommand : GenerateCommand).ExecuteAsync(null));
        ActiveRegenerateCommand = new AsyncRelayCommand(
            async () => await (IsWeeklyView ? WeeklyRegenerateCommand : RegenerateCommand).ExecuteAsync(null));
        ActiveRefreshCommand = new AsyncRelayCommand(
            async () => await (IsWeeklyView ? WeeklyRefreshCommand : RefreshCommand).ExecuteAsync(null));
        ActiveExportCommand = new AsyncRelayCommand(
            async () => await (IsWeeklyView ? ExportWeeklyCommand : ExportCommand).ExecuteAsync(null));
        ShowDailyCommand = new RelayCommand(() =>
        {
            IsWeeklyView = false;
            CollapseScheduleEditors();
        });
        ShowWeeklyCommand = new RelayCommand(() =>
        {
            IsWeeklyView = true;
            CollapseScheduleEditors();
        });
        Versions.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasVersions));
            OnPropertyChanged(nameof(ShowDailyStatus));
        };
        WeeklyVersions.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasWeeklyVersions));
            OnPropertyChanged(nameof(ShowWeeklyStatus));
        };
    }

    public ObservableCollection<ReportVersionItem> Versions { get; } = [];

    /// <summary>True when the selected date already has at least one report version.</summary>
    public bool HasVersions => Versions.Count > 0;

    /// <summary>底部状态行：既要当前模式匹配，也要确实有版本，避免与空态重复。</summary>
    public bool ShowDailyStatus => ShowDailyView && HasVersions;

    public bool ShowWeeklyStatus => IsWeeklyView && HasWeeklyVersions;

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand GenerateCommand { get; }
    public IAsyncRelayCommand RegenerateCommand { get; }
    public IAsyncRelayCommand ExportCommand { get; }
    public IAsyncRelayCommand SaveScheduleCommand { get; }

    /// <summary>生成周报：本周已有版本时直接复用，不重复调用模型。</summary>
    public IAsyncRelayCommand WeeklyReportCommand { get; }

    /// <summary>强制重新生成周报：总是新增一个版本。</summary>
    public IAsyncRelayCommand WeeklyRegenerateCommand { get; }

    /// <summary>刷新周报版本列表（重新读库）。</summary>
    public IAsyncRelayCommand WeeklyRefreshCommand { get; }

    public IAsyncRelayCommand ExportWeeklyCommand { get; }

    public IAsyncRelayCommand SaveWeeklyScheduleCommand { get; }

    /// <summary>当前视图对应的四个操作：日报/周报共用同一组按钮。</summary>
    public IAsyncRelayCommand ActiveGenerateCommand { get; }

    public IAsyncRelayCommand ActiveRegenerateCommand { get; }

    public IAsyncRelayCommand ActiveRefreshCommand { get; }

    public IAsyncRelayCommand ActiveExportCommand { get; }

    public IRelayCommand ShowDailyCommand { get; }

    public IRelayCommand ShowWeeklyCommand { get; }

    public ObservableCollection<DailyBarItem> WeeklyBars { get; } = [];

    public ObservableCollection<WeeklyVersionItem> WeeklyVersions { get; } = [];

    public bool HasWeeklyVersions => WeeklyVersions.Count > 0;

    /// <summary>周报版本选择：和日报版本列表行为一致，选中即切换正文。</summary>
    public WeeklyVersionItem? SelectedWeeklyVersion
    {
        get => _selectedWeeklyVersion;
        set
        {
            if (SetProperty(ref _selectedWeeklyVersion, value))
            {
                _selectedWeekly = value?.Report;
                WeeklyMarkdown = value?.Report.Markdown ?? string.Empty;
                WeeklyBars.Clear();
                if (value is not null &&
                    WeeklyReportUseCase.ToRangeReport(value.Report) is { } payload)
                {
                    // 旧版本或损坏的 payload 可能缺 DailyStats：柱状图退化为空，不影响正文。
                    foreach (var day in payload.DailyStats ?? [])
                    {
                        WeeklyBars.Add(new DailyBarItem(day, payload.MaxDailyEvents));
                    }
                }

                OnPropertyChanged(nameof(HasWeeklyReport));
                OnPropertyChanged(nameof(WeeklyVersionText));
                OnPropertyChanged(nameof(CanExport));
                ExportWeeklyCommand.NotifyCanExecuteChanged();
            }
        }
    }

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
                OnPropertyChanged(nameof(CanExport));
                OnPropertyChanged(nameof(ShowDailyStatus));
                OnPropertyChanged(nameof(ShowWeeklyStatus));
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

    public bool HasWeeklyReport => _selectedWeekly is not null;

    public string WeeklyVersionText => _selectedWeekly is null
        ? string.Empty
        : $"v{_selectedWeekly.Version} · {_selectedWeekly.WeekStart:MM-dd}~{_selectedWeekly.WeekEnd:MM-dd} · {_selectedWeekly.ModelId} · {_selectedWeekly.GeneratedAtUtc.ToLocalTime():MM-dd HH:mm}";

    /// <summary>自动周报：周几 + 时间，与自动日报共用同一个设置文件。</summary>
    /// <summary>自动日报设置块的展开状态：双向绑定到 Expander；保存或切换模式后收起。</summary>
    public bool IsDailyScheduleExpanded
    {
        get => _isDailyScheduleExpanded;
        set => SetProperty(ref _isDailyScheduleExpanded, value);
    }

    public bool IsWeeklyScheduleExpanded
    {
        get => _isWeeklyScheduleExpanded;
        set => SetProperty(ref _isWeeklyScheduleExpanded, value);
    }

    public bool WeeklyScheduleEnabled
    {
        get => _weeklyScheduleEnabled;
        set
        {
            if (SetProperty(ref _weeklyScheduleEnabled, value))
            {
                OnPropertyChanged(nameof(WeeklyScheduleSummary));
            }
        }
    }

    public WeekdayOption WeeklyScheduleDay
    {
        get => _weeklyScheduleDay;
        set
        {
            if (SetProperty(ref _weeklyScheduleDay, value))
            {
                OnPropertyChanged(nameof(WeeklyScheduleSummary));
            }
        }
    }

    public TimeSpan? WeeklyScheduleTime
    {
        get => _weeklyScheduleTime;
        set
        {
            if (SetProperty(ref _weeklyScheduleTime, value))
            {
                OnPropertyChanged(nameof(WeeklyScheduleSummary));
            }
        }
    }

    public string WeeklyScheduleStatus
    {
        get => _weeklyScheduleStatus;
        private set
        {
            if (SetProperty(ref _weeklyScheduleStatus, value))
            {
                OnPropertyChanged(nameof(WeeklyScheduleSummary));
            }
        }
    }

    public string WeeklyScheduleSummary => WeeklyScheduleEnabled
        ? $"已启用 · 每周{WeeklyScheduleDay.Label} {WeeklyScheduleTime?.ToString(@"hh\:mm", CultureInfo.InvariantCulture) ?? "--:--"}"
        : "已关闭";

    public IReadOnlyList<WeekdayOption> WeekdayOptions { get; } = WeekdayOption.All;

    public bool HasScheduleSettings => _scheduleSettings is not null;

    /// <summary>Local wall-clock time for the automatic previous-day check.</summary>
    public TimeSpan? ScheduleTime
    {
        get => _scheduleTime;
        set
        {
            if (SetProperty(ref _scheduleTime, value))
            {
                OnPropertyChanged(nameof(ScheduleSummary));
            }
        }
    }

    public bool ScheduleEnabled
    {
        get => _scheduleEnabled;
        set
        {
            if (SetProperty(ref _scheduleEnabled, value))
            {
                OnPropertyChanged(nameof(ScheduleSummary));
            }
        }
    }

    public bool IsScheduleSaving
    {
        get => _isScheduleSaving;
        private set => SetProperty(ref _isScheduleSaving, value);
    }

    public string ScheduleStatusMessage
    {
        get => _scheduleStatusMessage;
        private set
        {
            if (SetProperty(ref _scheduleStatusMessage, value))
            {
                OnPropertyChanged(nameof(ScheduleSummary));
            }
        }
    }

    /// <summary>折叠状态下的摘要：一眼看出自动日报是否开启、几点执行。</summary>
    public string ScheduleSummary => ScheduleEnabled
        ? $"已启用 · 每天 {ScheduleTime?.ToString(@"hh\:mm", CultureInfo.InvariantCulture) ?? "--:--"}"
        : "已关闭";

    public DateTimeOffset? SelectedDate
    {
        get => _selectedDate;
        set
        {
            if (SetProperty(ref _selectedDate, value))
            {
                _ = RefreshAsync();
                _ = RefreshWeeklyAsync();
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
                OnPropertyChanged(nameof(CanExport));
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

    /// <summary>当前视图是否有可导出的版本；没有时「导出」应禁用而不是静默无响应。</summary>
    public bool CanExport => IsWeeklyView ? _selectedWeekly is not null : SelectedVersion is not null;

    /// <summary>Save-file dialog, wired by the shell after the window exists; null falls back to the export directory.</summary>
    public ISaveFilePicker? SaveFilePicker { get; set; }

    public string VersionText => SelectedVersion is null
        ? string.Empty
        : $"v{SelectedVersion.Report.Version} · {SelectedVersion.Report.ModelId} · {SelectedVersion.Report.GeneratedAtUtc.ToLocalTime():MM-dd HH:mm}";

    public async Task InitializeAsync()
    {
        LoadScheduleSettings();
        LoadWeeklyScheduleSettings();
        await RefreshAsync();
        await RefreshWeeklyAsync();
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
            ScheduleStatusMessage = "已保存";
            IsDailyScheduleExpanded = false;
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

    /// <summary>切换 日报/周报 时两块都收起，避免下次进入时仍是展开态。</summary>
    private void CollapseScheduleEditors()
    {
        IsDailyScheduleExpanded = false;
        IsWeeklyScheduleExpanded = false;
    }

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
    /// force=false 时若本周已有版本则直接复用，与日报"生成 / 强制重生成"的语义一致。
    /// </summary>
    private async Task GenerateWeeklyAsync(bool force)
    {
        if (_weekly is null || IsBusy || SelectedDate is null)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        WeeklyStatus = force ? "正在强制重新生成周报…" : "正在生成周报…";
        try
        {
            var anchor = DateOnly.FromDateTime(SelectedDate.Value.Date);
            var before = WeeklyVersions.Count;
            var report = await _weekly.GenerateAsync(anchor, force, ReportTriggerSource.Manual, CancellationToken.None);
            await RefreshWeeklyAsync();
            SelectedWeeklyVersion = WeeklyVersions.FirstOrDefault(item => item.Report.Id == report.Id)
                ?? WeeklyVersions.FirstOrDefault();
            WeeklyStatus = WeeklyVersions.Count == before && !force
                ? $"本周已有周报 v{report.Version}，未重新生成"
                : $"周报 v{report.Version} 已生成";
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

    private async Task RefreshWeeklyAsync()
    {
        if (_weekly is null || SelectedDate is null)
        {
            return;
        }

        try
        {
            var versions = await _weekly.ListVersionsAsync(
                DateOnly.FromDateTime(SelectedDate.Value.Date),
                CancellationToken.None);
            var previous = SelectedWeeklyVersion?.Report.Id;
            WeeklyVersions.Clear();
            foreach (var report in versions)
            {
                WeeklyVersions.Add(new WeeklyVersionItem(report));
            }

            SelectedWeeklyVersion = WeeklyVersions.FirstOrDefault(item => item.Report.Id == previous)
                ?? WeeklyVersions.FirstOrDefault();
            WeeklyStatus = WeeklyVersions.Count == 0
                ? "本周还没有周报"
                : $"本周 {WeeklyVersions.Count} 个周报版本";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Core.Exceptions.AiAssistantDomainException)
        {
            ErrorMessage = "读取周报失败，请检查加密数据库状态。";
        }
    }

    private async Task ExportWeeklyAsync()
    {
        if (_range is null || _selectedWeekly is null || IsBusy)
        {
            return;
        }

        var report = WeeklyReportUseCase.ToRangeReport(_selectedWeekly);
        if (report is null)
        {
            ErrorMessage = "周报内容无法解析，无法导出。";
            return;
        }

        try
        {
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

    private void LoadWeeklyScheduleSettings()
    {
        if (_scheduleSettings is null)
        {
            return;
        }

        try
        {
            var settings = _scheduleSettings.Get();
            WeeklyScheduleEnabled = settings.WeeklyEnabled;
            WeeklyScheduleDay = WeekdayOption.From(settings.WeeklyDay);
            WeeklyScheduleTime = settings.WeeklyLocalTime.ToTimeSpan();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            WeeklyScheduleStatus = "读取自动周报设置失败。";
        }
    }

    private async Task SaveWeeklyScheduleAsync()
    {
        if (_scheduleSettings is null || IsScheduleSaving)
        {
            return;
        }

        if (WeeklyScheduleTime is null)
        {
            WeeklyScheduleStatus = "请选择自动周报时间。";
            return;
        }

        IsScheduleSaving = true;
        try
        {
            var time = TimeOnly.FromTimeSpan(WeeklyScheduleTime.Value);
            await _scheduleSettings.SaveWeeklyAsync(WeeklyScheduleEnabled, WeeklyScheduleDay.Day, time);
            WeeklyScheduleStatus = "已保存";
            IsWeeklyScheduleExpanded = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            WeeklyScheduleStatus = "保存自动周报设置失败，请检查数据目录权限。";
        }
        finally
        {
            IsScheduleSaving = false;
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

/// <summary>周报版本列表项：与日报版本项同构，便于两个视图保持一致的交互。</summary>
public sealed class WeeklyVersionItem(WeeklyReport report)
{
    public WeeklyReport Report { get; } = report;

    public string DisplayText { get; } = string.Create(
        CultureInfo.InvariantCulture,
        $"v{report.Version} · {report.WeekStart:MM-dd}~{report.WeekEnd:MM-dd} · {DescribeTrigger(report.TriggerSource)} · {report.GeneratedAtUtc.ToLocalTime():MM-dd HH:mm}");

    private static string DescribeTrigger(ReportTriggerSource source) => source switch
    {
        ReportTriggerSource.Manual => "手动",
        ReportTriggerSource.StartupCatchUp => "启动补偿",
        ReportTriggerSource.ScheduledDaily => "定时",
        ReportTriggerSource.ScheduledWeekly => "自动周报",
        ReportTriggerSource.VoiceCommand => "语音",
        _ => "其他"
    };
}

/// <summary>星期选项：自动周报的"周几"下拉框。</summary>
public sealed record WeekdayOption(DayOfWeek Day, string Label)
{
    public static readonly IReadOnlyList<WeekdayOption> All =
    [
        new(DayOfWeek.Monday, "一"),
        new(DayOfWeek.Tuesday, "二"),
        new(DayOfWeek.Wednesday, "三"),
        new(DayOfWeek.Thursday, "四"),
        new(DayOfWeek.Friday, "五"),
        new(DayOfWeek.Saturday, "六"),
        new(DayOfWeek.Sunday, "日")
    ];

    public static WeekdayOption From(DayOfWeek day) => All.First(option => option.Day == day);
}
