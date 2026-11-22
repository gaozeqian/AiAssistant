using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AiAssistant.Application.UseCases;
using AiAssistant.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiAssistant.Desktop.ViewModels;

public sealed class ReportsViewModel : ObservableObject
{
    private readonly ReportUseCase _reports;
    private readonly string _exportDirectory;
    private DateTimeOffset? _selectedDate = DateTimeOffset.Now.Date;
    private ReportVersionItem? _selectedVersion;
    private string _markdown = string.Empty;
    private string _standupText = "尚无站会数据";
    private string _statusMessage = "选择日期后可生成或查看日报";
    private string _errorMessage = string.Empty;
    private bool _isBusy;

    public ReportsViewModel(ReportUseCase reports, string exportDirectory)
    {
        _reports = reports ?? throw new ArgumentNullException(nameof(reports));
        _exportDirectory = exportDirectory ?? throw new ArgumentNullException(nameof(exportDirectory));
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        GenerateCommand = new AsyncRelayCommand(() => GenerateAsync(forceOverwrite: false));
        RegenerateCommand = new AsyncRelayCommand(() => GenerateAsync(forceOverwrite: true));
        ExportCommand = new AsyncRelayCommand(ExportAsync);
    }

    public ObservableCollection<ReportVersionItem> Versions { get; } = [];

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand GenerateCommand { get; }
    public IAsyncRelayCommand RegenerateCommand { get; }
    public IAsyncRelayCommand ExportCommand { get; }

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
        private set => SetProperty(ref _markdown, value);
    }

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
        private set => SetProperty(ref _isBusy, value);
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool HasReport => SelectedVersion is not null;

    public string VersionText => SelectedVersion is null
        ? string.Empty
        : $"v{SelectedVersion.Report.Version} · {SelectedVersion.Report.ModelId} · {SelectedVersion.Report.GeneratedAtUtc.ToLocalTime():MM-dd HH:mm}";

    public async Task InitializeAsync() => await RefreshAsync();

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
            var path = await ReportUseCase.ExportMarkdownAsync(SelectedVersion.Report, _exportDirectory);
            StatusMessage = $"已导出：{path}";
            ErrorMessage = string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = "导出失败，请检查目标目录权限。";
        }
    }

    private static string FormatStandup(string statsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(statsJson);
            if (!document.RootElement.TryGetProperty("Standup", out var standup) &&
                !document.RootElement.TryGetProperty("standup", out standup))
            {
                return "尚无站会数据";
            }

            static string ReadString(JsonElement element, string property) =>
                element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? string.Empty
                    : string.Empty;

            var blockers = standup.TryGetProperty("Blockers", out var blockerArray) &&
                           blockerArray.ValueKind == JsonValueKind.Array
                ? string.Join("；", blockerArray.EnumerateArray().Select(item => item.GetString()).Where(item => !string.IsNullOrWhiteSpace(item)))
                : string.Empty;
            var builder = new StringBuilder();
            builder.AppendLine(CultureInfo.InvariantCulture, $"昨天：{ReadString(standup, "Yesterday")}");
            builder.AppendLine(CultureInfo.InvariantCulture, $"今天：{ReadString(standup, "Today")}");
            builder.Append(CultureInfo.InvariantCulture, $"阻塞：{(string.IsNullOrWhiteSpace(blockers) ? "无" : blockers)}");
            return builder.ToString();
        }
        catch (JsonException)
        {
            return "站会数据格式不可用";
        }
    }
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
