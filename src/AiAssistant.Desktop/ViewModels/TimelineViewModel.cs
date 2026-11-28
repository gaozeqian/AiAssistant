using System.Collections.ObjectModel;
using System.Globalization;
using AiAssistant.Application.Ports;
using AiAssistant.Application.UseCases;
using AiAssistant.Core.Domain;
using AiAssistant.Core.Exceptions;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiAssistant.Desktop.ViewModels;

public sealed class TimelineViewModel : ObservableObject, IDisposable
{
    private readonly TimelineUseCase _timeline;
    private readonly TimelinePreviewLoader _previewLoader;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly ProgressPulseController _progressPulse;
    private CancellationTokenSource? _queryCts;
    private CancellationTokenSource? _loadMoreCts;
    private CancellationTokenSource? _mediaCts;
    private TimelineDateItem? _selectedDate;
    private TimelineEventItem? _selectedEvent;
    private Bitmap? _previewImage;
    private string _searchText = string.Empty;
    private string _statusMessage = "正在读取本地时间线…";
    private string _errorMessage = string.Empty;
    private string _previewError = string.Empty;
    private bool _isLoading;
    private bool _isLoadingMore;
    private bool _isPreviewLoading;
    private bool _hasMore;
    private bool _suppressDateSelection;
    private bool _disposed;
    private double _progressPulseValue;

    public TimelineViewModel(TimelineUseCase timeline)
    {
        _timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
        _previewLoader = new TimelinePreviewLoader(_timeline);
        _progressPulse = new ProgressPulseController(value => ProgressPulse = value);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        LoadMoreCommand = new AsyncRelayCommand(LoadEarlierAsync);
        // 空态只跟"有没有数据"走：没有这条通知，界面会在有 120 条记录、只是没选中时
        // 依然显示「暂无可显示的活动」并压在列表上。
        Events.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasEvents));
            OnPropertyChanged(nameof(ShowSelectionHint));
        };
    }

    public ObservableCollection<TimelineDateItem> ActiveDates { get; } = [];

    public ObservableCollection<TimelineEventItem> Events { get; } = [];

    public IAsyncRelayCommand RefreshCommand { get; }

    /// <summary>Loads the next (older) page and appends it below the list, keeping the current selection.</summary>
    public IAsyncRelayCommand LoadMoreCommand { get; }

    public bool HasMore
    {
        get => _hasMore;
        private set => SetProperty(ref _hasMore, value);
    }

    public bool IsLoadingMore
    {
        get => _isLoadingMore;
        private set
        {
            if (SetProperty(ref _isLoadingMore, value))
            {
                SyncProgressPulse();
            }
        }
    }

    public TimelineDateItem? SelectedDate
    {
        get => _selectedDate;
        set
        {
            if (SetProperty(ref _selectedDate, value) && !_suppressDateSelection)
            {
                _ = LoadSelectedDateSafelyAsync();
            }
        }
    }

    public TimelineEventItem? SelectedEvent
    {
        get => _selectedEvent;
        set
        {
            if (SetProperty(ref _selectedEvent, value))
            {
                OnPropertyChanged(nameof(HasSelectedEvent));
                OnPropertyChanged(nameof(ShowSelectionHint));
                OnPropertyChanged(nameof(HasNoMedia));
                _ = LoadPreviewSafelyAsync(value);
            }
        }
    }

    public Bitmap? PreviewImage
    {
        get => _previewImage;
        private set
        {
            if (ReferenceEquals(_previewImage, value))
            {
                return;
            }

            _previewImage?.Dispose();
            _previewImage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasPreview));
        }
    }

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
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

    public string PreviewError
    {
        get => _previewError;
        private set
        {
            if (SetProperty(ref _previewError, value))
            {
                OnPropertyChanged(nameof(HasPreviewError));
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                SyncProgressPulse();
            }
        }
    }

    public bool IsPreviewLoading
    {
        get => _isPreviewLoading;
        private set
        {
            if (SetProperty(ref _isPreviewLoading, value))
            {
                SyncProgressPulse();
            }
        }
    }

    /// <summary>Determinate 0..100 progress driven while any of the list/preview loads run.</summary>
    public double ProgressPulse
    {
        get => _progressPulseValue;
        private set => SetProperty(ref _progressPulseValue, value);
    }

    /// <summary>Three independent bars share one pulse, so it stops only when every load is done.</summary>
    private void SyncProgressPulse() => _progressPulse.SetBusy(IsLoading || IsLoadingMore || IsPreviewLoading);

    public bool HasSelectedEvent => SelectedEvent is not null;

    /// <summary>True when the current date actually has rows; drives the list's empty state.</summary>
    public bool HasEvents => Events.Count > 0;

    /// <summary>
    /// True only when there is something to select but nothing selected. When the day is empty the
    /// detail pane must not tell the user to "select an event on the left".
    /// </summary>
    public bool ShowSelectionHint => HasEvents && SelectedEvent is null;

    public bool HasNoMedia => SelectedEvent is { HasMedia: false };

    public bool HasPreview => PreviewImage is not null;

    public bool HasPreviewError => !string.IsNullOrEmpty(PreviewError);

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public async Task InitializeAsync() => await RefreshAsync();

    /// <summary>Jumps from a search/QA evidence citation to the exact timeline row, loading its date on demand.</summary>
    public async Task NavigateToEventAsync(DateOnly date, string eventId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        if (_disposed)
        {
            return;
        }

        var target = ActiveDates.FirstOrDefault(item => item.Date == date);
        if (target is null)
        {
            target = new TimelineDateItem(date);
            var index = 0;
            while (index < ActiveDates.Count && ActiveDates[index].Date > date)
            {
                index++;
            }

            ActiveDates.Insert(index, target);
        }

        _suppressDateSelection = true;
        SelectedDate = target;
        _suppressDateSelection = false;
        try
        {
            await LoadEventsAsync();
            await EnsureEventLoadedAsync(eventId);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is AiAssistantDomainException or IOException or UnauthorizedAccessException)
        {
            ErrorMessage = "无法加载证据所在日期，请稍后重试。";
            StatusMessage = "定位失败";
            return;
        }

        SelectedEvent = Events.FirstOrDefault(item => item.Event.Id == eventId) ?? SelectedEvent;
        StatusMessage = SelectedEvent?.Event.Id == eventId
            ? "已定位到证据对应的活动记录。"
            : "该记录不在当前日期列表中，可能已被保留策略清理。";
    }

    /// <summary>Pages backwards until the cited event is materialized (or the day is exhausted).</summary>
    private async Task EnsureEventLoadedAsync(string eventId)
    {
        while (HasMore &&
               !_disposed &&
               Events.All(item => !string.Equals(item.Event.Id, eventId, StringComparison.Ordinal)))
        {
            await LoadEarlierCoreAsync();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ResetCancellation(ref _queryCts);
        ResetCancellation(ref _loadMoreCts);
        ResetCancellation(ref _mediaCts);
        _progressPulse.Dispose();
        PreviewImage = null;
        _disposeCts.Cancel();
        _disposeCts.Dispose();
    }

    private async Task RefreshAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var dates = await _timeline.ListActiveDatesAsync(_disposeCts.Token);
            var today = _timeline.GetTodayLocalDate();
            var allDates = dates.Contains(today) ? dates : dates.Concat([today]);
            var orderedDates = allDates.Distinct().OrderByDescending(date => date).ToArray();
            var selected = SelectedDate?.Date ?? today;

            _suppressDateSelection = true;
            ActiveDates.Clear();
            foreach (var date in orderedDates)
            {
                ActiveDates.Add(new TimelineDateItem(date));
            }

            SelectedDate = ActiveDates.FirstOrDefault(item => item.Date == selected) ?? ActiveDates.FirstOrDefault();
            _suppressDateSelection = false;
            await LoadEventsAsync();
        }
        catch (OperationCanceledException ex) when (_disposeCts.IsCancellationRequested)
        {
            // 释放时取消属预期，静默返回。
            AiAssistant.Application.Diagnostics.SuppressedExceptionLog.Trace("Desktop.TimelineViewModel.RefreshAsync", ex);
        }
        catch (Exception ex) when (ex is AiAssistantDomainException or IOException or UnauthorizedAccessException)
        {
            ErrorMessage = "时间线暂时不可用。请检查本地存储权限或打开诊断页。";
            StatusMessage = "读取失败";
        }
        finally
        {
            _suppressDateSelection = false;
            IsLoading = false;
        }
    }

    private async Task LoadSelectedDateSafelyAsync()
    {
        try
        {
            await LoadEventsAsync();
        }
        catch (OperationCanceledException ex)
        {
            // 切换日期时取消上一次查询属预期，静默降级。
            AiAssistant.Application.Diagnostics.SuppressedExceptionLog.Trace("Desktop.TimelineViewModel.LoadSelectedDateSafelyAsync", ex);
        }
        catch (Exception ex) when (ex is AiAssistantDomainException or IOException or UnauthorizedAccessException)
        {
            ErrorMessage = "无法读取所选日期，请稍后重试。";
            StatusMessage = "读取失败";
        }
    }

    private async Task LoadEventsAsync()
    {
        if (SelectedDate is null || _disposed)
        {
            Events.Clear();
            SelectedEvent = null;
            HasMore = false;
            return;
        }

        ResetCancellation(ref _queryCts);
        _queryCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
        var ct = _queryCts.Token;
        IsLoading = true;
        ErrorMessage = string.Empty;

        try
        {
            // Page 0 is the most recent slice; the list renders newest-first, so the top row is
            // the latest activity and older pages append below.
            var page = await _timeline.GetEventsForDateAsync(
                SelectedDate.Date,
                SearchText,
                before: null,
                TimelineUseCase.DefaultPageSize,
                ct);
            ct.ThrowIfCancellationRequested();
            Events.Clear();
            foreach (var item in page.Events)
            {
                Events.Add(new TimelineEventItem(item));
            }

            HasMore = page.HasMore;
            SelectedEvent = Events.FirstOrDefault();
            UpdateStatusMessage();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadEarlierAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await LoadEarlierCoreAsync();
        }
        catch (OperationCanceledException ex)
        {
            // 切换日期或刷新时取消分页属预期，静默降级。
            AiAssistant.Application.Diagnostics.SuppressedExceptionLog.Trace("Desktop.TimelineViewModel.LoadEarlierAsync", ex);
        }
        catch (Exception ex) when (ex is AiAssistantDomainException or IOException or UnauthorizedAccessException)
        {
            ErrorMessage = "无法加载更早的记录，请稍后重试。";
            StatusMessage = "读取失败";
        }
    }

    /// <summary>
    /// Appends the next older page below the list. Paging is keyset-based (cursor = last loaded
    /// row), so records captured while the user pages never shift or duplicate the loaded rows.
    /// </summary>
    private async Task LoadEarlierCoreAsync()
    {
        if (SelectedDate is null || _disposed || !HasMore || IsLoadingMore)
        {
            return;
        }

        var lastLoaded = Events.LastOrDefault()?.Event;
        var cursor = lastLoaded is null
            ? null
            : new EventCursor(lastLoaded.Timestamp.OccurredAtUtc, lastLoaded.Id);

        ResetCancellation(ref _loadMoreCts);
        _loadMoreCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
        var ct = _loadMoreCts.Token;
        IsLoadingMore = true;
        try
        {
            var page = await _timeline.GetEventsForDateAsync(
                SelectedDate.Date,
                SearchText,
                cursor,
                TimelineUseCase.DefaultPageSize,
                ct);
            ct.ThrowIfCancellationRequested();

            foreach (var item in page.Events)
            {
                Events.Add(new TimelineEventItem(item));
            }

            HasMore = page.HasMore;
            UpdateStatusMessage();
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    private void UpdateStatusMessage()
    {
        if (Events.Count == 0)
        {
            StatusMessage = "这一天还没有符合条件的记录";
            return;
        }

        StatusMessage = HasMore
            ? $"已加载 {Events.Count} 条事件（还有更早的记录）· 点击记录查看加密媒体"
            : $"已加载 {Events.Count} 条事件 · 点击记录查看加密媒体";
    }

    private async Task LoadPreviewSafelyAsync(TimelineEventItem? item)
    {
        ResetCancellation(ref _mediaCts);
        PreviewImage = null;
        PreviewError = string.Empty;
        IsPreviewLoading = false;

        if (item?.Event.MediaRef is null || _disposed)
        {
            return;
        }

        _mediaCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
        var ct = _mediaCts.Token;
        var eventId = item.Event.Id;
        IsPreviewLoading = true;
        try
        {
            var result = await _previewLoader.LoadAsync(item.Event, ct);
            if (SelectedEvent?.Event.Id == eventId)
            {
                PreviewImage = result.Image;
                PreviewError = result.Error;
            }
            else
            {
                result.Image?.Dispose();
            }
        }
        catch (OperationCanceledException ex)
        {
            // 切换选中项时取消上一次媒体加载属预期，静默降级。
            AiAssistant.Application.Diagnostics.SuppressedExceptionLog.Trace("Desktop.TimelineViewModel.LoadPreviewSafelyAsync", ex);
        }
        catch (Exception ex)
        {
            // Fire-and-forget callers discard this task, so an unexpected failure must be surfaced
            // in the UI instead of vanishing as an unobserved task exception.
            if (SelectedEvent?.Event.Id == eventId)
            {
                PreviewError = "媒体预览加载失败，请稍后重试。";
            }

            AiAssistant.Application.Diagnostics.SuppressedExceptionLog.Trace("Desktop.TimelineViewModel.LoadPreviewSafelyAsync", ex);
        }
        finally
        {
            if (SelectedEvent?.Event.Id == eventId)
            {
                IsPreviewLoading = false;
            }
        }
    }

    /// <summary>
    /// Cancels, disposes and clears a CTS field. Disposing without clearing left a disposed
    /// instance behind; the next Cancel() then threw ObjectDisposedException, which silently
    /// broke media preview after a refresh.
    /// </summary>
    private static void ResetCancellation(ref CancellationTokenSource? field)
    {
        var cts = field;
        field = null;
        cts?.Cancel();
        cts?.Dispose();
    }
}
