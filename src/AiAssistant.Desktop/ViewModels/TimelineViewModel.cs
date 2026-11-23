using System.Collections.ObjectModel;
using System.Globalization;
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
    private CancellationTokenSource? _queryCts;
    private CancellationTokenSource? _mediaCts;
    private TimelineDateItem? _selectedDate;
    private TimelineEventItem? _selectedEvent;
    private Bitmap? _previewImage;
    private string _searchText = string.Empty;
    private string _statusMessage = "正在读取本地时间线…";
    private string _errorMessage = string.Empty;
    private string _previewError = string.Empty;
    private bool _isLoading;
    private bool _isPreviewLoading;
    private bool _suppressDateSelection;
    private bool _disposed;

    public TimelineViewModel(TimelineUseCase timeline)
    {
        _timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
        _previewLoader = new TimelinePreviewLoader(_timeline);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
    }

    public ObservableCollection<TimelineDateItem> ActiveDates { get; } = [];

    public ObservableCollection<TimelineEventItem> Events { get; } = [];

    public IAsyncRelayCommand RefreshCommand { get; }

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
        private set => SetProperty(ref _isLoading, value);
    }

    public bool IsPreviewLoading
    {
        get => _isPreviewLoading;
        private set => SetProperty(ref _isPreviewLoading, value);
    }

    public bool HasSelectedEvent => SelectedEvent is not null;

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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queryCts?.Cancel();
        _mediaCts?.Cancel();
        _queryCts?.Dispose();
        _mediaCts?.Dispose();
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
            return;
        }

        _queryCts?.Cancel();
        _queryCts?.Dispose();
        _queryCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
        var ct = _queryCts.Token;
        IsLoading = true;
        ErrorMessage = string.Empty;

        try
        {
            var events = await _timeline.GetEventsForDateAsync(SelectedDate.Date, SearchText, ct);
            ct.ThrowIfCancellationRequested();
            Events.Clear();
            foreach (var item in events)
            {
                Events.Add(new TimelineEventItem(item));
            }

            SelectedEvent = Events.LastOrDefault();
            StatusMessage = Events.Count == 0
                ? "这一天还没有符合条件的记录"
                : $"已加载 {Events.Count} 条事件 · 点击记录查看加密媒体";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadPreviewSafelyAsync(TimelineEventItem? item)
    {
        _mediaCts?.Cancel();
        _mediaCts?.Dispose();
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
        finally
        {
            if (SelectedEvent?.Event.Id == eventId)
            {
                IsPreviewLoading = false;
            }
        }
    }
}
