using System.Globalization;
using AiAssistant.Application.Ports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiAssistant.Desktop.ViewModels;

public enum PendingDeletion
{
    None = 0,
    Date = 1,
    All = 2
}

/// <summary>Two-step confirmed deletion of local captured data. Export entry points live in the reports and search panels.</summary>
public sealed class DataManagementViewModel : ObservableObject
{
    private readonly IDataDeletionService _deletion;
    private DateTimeOffset? _selectedDate = new(DateTime.Today, TimeSpan.Zero);
    private PendingDeletion _pending;
    private string _statusMessage = "删除不可撤销；加密媒体与结构化事件会同时移除。";
    private string _errorMessage = string.Empty;
    private bool _isBusy;

    public DataManagementViewModel(IDataDeletionService deletion)
    {
        _deletion = deletion ?? throw new ArgumentNullException(nameof(deletion));
        DeleteDateCommand = new AsyncRelayCommand(DeleteDateAsync);
        DeleteAllCommand = new AsyncRelayCommand(DeleteAllAsync);
        CancelPendingCommand = new RelayCommand(CancelPending);
    }

    public IAsyncRelayCommand DeleteDateCommand { get; }

    public IAsyncRelayCommand DeleteAllCommand { get; }

    public IRelayCommand CancelPendingCommand { get; }

    public DateTimeOffset? SelectedDate
    {
        get => _selectedDate;
        set => SetProperty(ref _selectedDate, value);
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

    public bool HasPending => _pending != PendingDeletion.None;

    public string PendingText => _pending switch
    {
        PendingDeletion.Date => string.Create(
            CultureInfo.InvariantCulture,
            $"将永久删除 {ResolveDate():yyyy-MM-dd} 的全部事件、加密媒体、日报与排队任务。再次点击“确认删除”继续。"),
        PendingDeletion.All => "将永久删除本机全部事件、媒体、日报、语音文本日志与向量索引，且无法恢复。再次点击“确认清空”继续。",
        _ => string.Empty
    };

    public string DeleteDateButtonText => _pending == PendingDeletion.Date ? "确认删除" : "删除该日数据";

    public string DeleteAllButtonText => _pending == PendingDeletion.All ? "确认清空" : "清空全部本地数据";

    public string RetentionNotice { get; } =
        "默认保留策略：结构化事件长期保留，加密媒体按写入时的保留期自动安全擦除；" +
        "删除后原始媒体无法恢复，问答与周报只会引用仍存在的结构化证据。";

    public string ExportNotice { get; } =
        "导出入口：日报页导出 Markdown、检索页导出问答与周报、诊断页生成脱敏导出预览并确认写出。所有导出都在本机生成。";

    private async Task DeleteDateAsync()
    {
        if (_pending != PendingDeletion.Date)
        {
            SetPending(PendingDeletion.Date);
            return;
        }

        await RunAsync(async ct =>
        {
            var result = await _deletion.PurgeDateAsync(ResolveDate(), ct);
            StatusMessage = string.Create(
                CultureInfo.InvariantCulture,
                $"已删除 {ResolveDate():yyyy-MM-dd}：事件 {result.EventsDeleted} 条、日报 {result.ReportsDeleted} 份、任务 {result.JobsDeleted} 个。");
        });
    }

    private async Task DeleteAllAsync()
    {
        if (_pending != PendingDeletion.All)
        {
            SetPending(PendingDeletion.All);
            return;
        }

        await RunAsync(async ct =>
        {
            var result = await _deletion.PurgeAllAsync(ct);
            StatusMessage = string.Create(
                CultureInfo.InvariantCulture,
                $"已清空本地数据：事件 {result.EventsDeleted} 条、媒体 {result.MediaFilesDeleted} 个、日报 {result.ReportsDeleted} 份、语音日志 {result.VoiceCommandsDeleted} 条。");
        });
    }

    private void CancelPending()
    {
        SetPending(PendingDeletion.None);
        StatusMessage = "已取消删除。";
    }

    private void SetPending(PendingDeletion pending)
    {
        _pending = pending;
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(PendingText));
        OnPropertyChanged(nameof(DeleteDateButtonText));
        OnPropertyChanged(nameof(DeleteAllButtonText));
    }

    private DateOnly ResolveDate() =>
        DateOnly.FromDateTime((SelectedDate ?? DateTimeOffset.Now).LocalDateTime);

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            await action(CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Core.Exceptions.AiAssistantDomainException)
        {
            ErrorMessage = "删除未完成。数据库可能被占用，请关闭其他实例后重试。";
            StatusMessage = "删除失败";
        }
        finally
        {
            SetPending(PendingDeletion.None);
            IsBusy = false;
        }
    }
}
