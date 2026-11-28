using System.Collections.ObjectModel;
using System.Globalization;
using AiAssistant.Application.Ports;
using AiAssistant.Application.UseCases;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiAssistant.Desktop.ViewModels;

public sealed record EvidenceNavigationRequest(DateOnly LocalDate, string EventId);

/// <summary>Hybrid search and evidence-grounded Q&amp;A. Weekly range reports live in the reports tab.</summary>
public sealed class SearchViewModel : ObservableObject, IDisposable
{
    private readonly EvidenceSearchUseCase _search;
    private readonly IClock _clock;
    private readonly string _exportDirectory;
    private readonly CancellationTokenSource _disposeCts = new();
    private DateTimeOffset? _fromDate;
    private DateTimeOffset? _toDate;
    private string _query = string.Empty;
    private string _answer = "输入问题后，回答只会引用可验证的本地证据。";
    private string _answerMeta = "尚无问答记录";
    private string _statusMessage = "默认检索近 7 天，可调整日期范围。";
    private string _errorMessage = string.Empty;
    private bool _isBusy;
    private double _progressPulseValue;
    private bool _disposed;
    private EvidenceQaAnswer? _lastAnswer;
    private readonly ProgressPulseController _progressPulse;

    public SearchViewModel(
        EvidenceSearchUseCase search,
        string exportDirectory,
        IClock? clock = null)
    {
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _exportDirectory = exportDirectory ?? throw new ArgumentNullException(nameof(exportDirectory));
        _clock = clock ?? new SystemClock();
        _progressPulse = new ProgressPulseController(value => ProgressPulse = value);
        var (from, to) = _search.DefaultRange();
        _fromDate = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        _toDate = new DateTimeOffset(to.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        SearchCommand = new AsyncRelayCommand(SearchAsync);
        AskCommand = new AsyncRelayCommand(AskAsync);
        ExportAnswerCommand = new AsyncRelayCommand(ExportAnswerAsync);
        // 空态跟着"有没有证据"走，而不是"有没有选中"。
        Evidence.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasEvidence));
            OnPropertyChanged(nameof(ShowEvidenceEmpty));
        };
    }

    public ObservableCollection<EvidenceCardItem> Evidence { get; } = [];

    /// <summary>True when the current query returned at least one evidence card.</summary>
    public bool HasEvidence => Evidence.Count > 0;

    /// <summary>Empty state for the evidence list: only when nothing is running, to avoid a flash.</summary>
    public bool ShowEvidenceEmpty => !HasEvidence && !IsBusy;

    public IAsyncRelayCommand SearchCommand { get; }

    public IAsyncRelayCommand AskCommand { get; }

    public IAsyncRelayCommand ExportAnswerCommand { get; }

    public event EventHandler<EvidenceNavigationRequest>? NavigationRequested;

    public DateTimeOffset? FromDate
    {
        get => _fromDate;
        set => SetProperty(ref _fromDate, value);
    }

    public DateTimeOffset? ToDate
    {
        get => _toDate;
        set => SetProperty(ref _toDate, value);
    }

    public string Query
    {
        get => _query;
        set => SetProperty(ref _query, value);
    }

    public string Answer
    {
        get => _answer;
        private set => SetProperty(ref _answer, value);
    }

    public string AnswerMeta
    {
        get => _answerMeta;
        private set => SetProperty(ref _answerMeta, value);
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
                OnPropertyChanged(nameof(ShowEvidenceEmpty));
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

    public bool HasAnswer => _lastAnswer is not null;

    /// <summary>Save-file dialog, wired by the shell after the window exists; null falls back to the export directory.</summary>
    public ISaveFilePicker? SaveFilePicker { get; set; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _progressPulse.Dispose();
        _disposeCts.Cancel();
        _disposeCts.Dispose();
    }

    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(Query))
        {
            ErrorMessage = "请输入要检索的关键词。";
            return;
        }

        await RunAsync(async ct =>
        {
            var result = await _search.SearchAsync(Query.Trim(), ResolveFrom(), ResolveTo(), ct);
            Evidence.Clear();
            foreach (var item in result.Evidence)
            {
                Evidence.Add(new EvidenceCardItem(item, NavigateTo));
            }

            StatusMessage = result.Evidence.Count == 0
                ? "没有找到匹配的本地证据；可扩大日期范围或更换关键词。"
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"命中 {result.Evidence.Count} 条证据（关键词候选 {result.KeywordCandidateCount}，向量候选 {result.VectorCandidateCount}）。");
        });
    }

    private async Task AskAsync()
    {
        if (string.IsNullOrWhiteSpace(Query))
        {
            ErrorMessage = "请输入要提问的问题。";
            return;
        }

        await RunAsync(async ct =>
        {
            var answer = await _search.AskAsync(Query.Trim(), ResolveFrom(), ResolveTo(), ct);
            _lastAnswer = answer;
            Answer = answer.Answer;
            AnswerMeta = string.Create(
                CultureInfo.InvariantCulture,
                $"模型 {answer.ModelId} · 引用 {(answer.CitedEvidenceIds.Count == 0 ? "无" : string.Join(",", answer.CitedEvidenceIds))} · {(answer.Degraded ? $"降级：{answer.DegradedReason}" : "证据充分")}");
            OnPropertyChanged(nameof(HasAnswer));
            Evidence.Clear();
            foreach (var item in answer.Evidence)
            {
                Evidence.Add(new EvidenceCardItem(item, NavigateTo));
            }

            StatusMessage = answer.HasEvidence
                ? "答案已附证据，点击证据可跳转到时间线。"
                : "没有可用证据，已明确降级而不是生成推测性回答。";
        });
    }

    private async Task ExportAnswerAsync()
    {
        if (_lastAnswer is null)
        {
            return;
        }

        await RunAsync(async ct =>
        {
            string path;
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
                    $"AiAssistant-问答-{_clock.LocalNow:yyyyMMdd-HHmmss}.md",
                    types);
                if (picked is null)
                {
                    return;
                }

                path = await _search.ExportAnswerSingleAsync(
                    _lastAnswer,
                    ReportExportFormatExtensions.FromExtension(picked.Extension),
                    picked.Path,
                    ct);
            }
            else
            {
                var export = await _search.ExportAnswerAllAsync(_lastAnswer, _exportDirectory, ct);
                path = export.MarkdownPath;
                StatusMessage = export.SvgPath is null
                    ? $"已导出问答证据（Markdown / JSON）：{path}"
                    : $"已导出问答证据（Markdown / SVG / PNG / PDF / JSON）：{path}";
                return;
            }

            StatusMessage = $"已导出问答证据：{path}";
        });
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_disposed)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            await action(_disposeCts.Token);
        }
        catch (OperationCanceledException ex) when (_disposeCts.IsCancellationRequested)
        {
            // 释放时取消属预期，无需向用户报错。
            AiAssistant.Application.Diagnostics.SuppressedExceptionLog.Trace("Desktop.SearchViewModel.RunAsync", ex);
        }
        catch (Exception ex)
        {
            // Surface the real reason (model unreachable, policy block, invalid output, ...).
            ErrorMessage = $"检索或生成失败：{ex.Message}";
            StatusMessage = "操作失败";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private DateOnly ResolveFrom() =>
        DateOnly.FromDateTime((FromDate ?? _clock.LocalNow.AddDays(-6)).LocalDateTime);

    private DateOnly ResolveTo() =>
        DateOnly.FromDateTime((ToDate ?? _clock.LocalNow).LocalDateTime);

    private void NavigateTo(EvidenceReference reference) =>
        NavigationRequested?.Invoke(this, new EvidenceNavigationRequest(reference.LocalDate, reference.EventId));
}

public sealed class EvidenceCardItem
{
    public EvidenceCardItem(EvidenceReference reference, Action<EvidenceReference> navigate)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(navigate);
        Reference = reference;
        NavigateCommand = new RelayCommand(() => navigate(reference));
    }

    public EvidenceReference Reference { get; }

    public IRelayCommand NavigateCommand { get; }

    public string EvidenceId => Reference.EvidenceId;

    public string Header => string.Create(
        CultureInfo.InvariantCulture,
        $"[{Reference.EvidenceId}] {Reference.LocalDate:yyyy-MM-dd} {Reference.OccurredAtUtc.ToLocalTime():HH:mm} · {Reference.AppName}");

    public string Title => Reference.Title;

    public string Excerpt => Reference.Excerpt;

    public string MatchedByText => string.Create(
        CultureInfo.InvariantCulture,
        $"召回：{MatchMethodLabel(Reference.MatchedBy)} · 分数 {Reference.Score:F4}");

    private static string MatchMethodLabel(string matchedBy) => matchedBy switch
    {
        "keyword+vector" => "关键词+向量",
        "vector" => "向量",
        "keyword" => "关键词",
        _ => matchedBy
    };

    public string MediaText => Reference.HasMedia
        ? "媒体仍在保留期内，可在时间线按需解密查看"
        : "原始媒体已清理，仅保留结构化证据";
}
