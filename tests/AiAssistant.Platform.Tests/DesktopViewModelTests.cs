using System.Runtime.CompilerServices;
using AiAssistant.Application.Policies;
using AiAssistant.Application.Ports;
using AiAssistant.Application.UseCases;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Desktop.ViewModels;
using AiAssistant.Desktop.Views;
using AiAssistant.Platform.Windows;
using Avalonia;
using Avalonia.Headless;

namespace AiAssistant.Platform.Tests;

public static class DesktopTestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Avalonia.Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class DesktopViewModelTests
{
    [Fact]
    public Task MainWindow_XamlLoadsInHeadlessSession() => RunInHeadlessSession(() =>
    {
        var window = new MainWindow();
        window.Show();
        Assert.Equal("时光 · AI 个人活动追踪助手", window.Title);
        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task MainWindow_CloseHidesAndCanRestoreFromTray() => RunInHeadlessSession(async () =>
    {
        using var shell = CreateShell();
        var window = new MainWindow { DataContext = shell };
        window.Show();
        await shell.InitializeAsync();

        window.Close();
        Assert.False(window.IsVisible);
        Assert.False(shell.IsExitRequested);

        window.ShowFromTray();
        Assert.True(window.IsVisible);
        shell.IsExitRequested = true;
        window.Close();
    });

    [Fact]
    public Task Timeline_DecryptsMediaOnlyAfterEventSelection() => RunInHeadlessSession(async () =>
    {
        var repository = new InMemoryEventRepository();
        var mediaStore = new FakeMediaStore();
        var clock = new SystemClock();
        var timestamp = ((IClock)clock).Now();
        repository.Events.Add(CreateEvent("without-media", timestamp));
        using var viewModel = new TimelineViewModel(new TimelineUseCase(repository, mediaStore, clock));

        await viewModel.InitializeAsync();
        Assert.Single(viewModel.Events);
        Assert.Equal(0, mediaStore.ReadCount);

        repository.Events.Add(CreateEvent("with-media", timestamp, "content-1"));
        await viewModel.RefreshCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => viewModel.PreviewImage is not null);

        Assert.Equal(1, mediaStore.ReadCount);
        Assert.Equal("content-1", mediaStore.LastReadContentId);
        Assert.True(viewModel.HasPreview);
    });

    [Fact]
    public Task Timeline_CancelledSelectionCannotOverwriteNewPreview() => RunInHeadlessSession(async () =>
    {
        var repository = new InMemoryEventRepository();
        var mediaStore = new FakeMediaStore { DelayReads = true };
        var clock = new SystemClock();
        var timestamp = ((IClock)clock).Now();
        repository.Events.Add(CreateEvent("without-media", timestamp));
        using var viewModel = new TimelineViewModel(new TimelineUseCase(repository, mediaStore, clock));
        await viewModel.InitializeAsync();

        viewModel.SelectedEvent = new TimelineEventItem(CreateEvent("first", timestamp, "content-1"));
        viewModel.SelectedEvent = new TimelineEventItem(CreateEvent("second", timestamp, "content-2"));
        mediaStore.CompleteRead("content-2");
        await WaitUntilAsync(() => viewModel.PreviewImage is not null);
        var currentPreview = viewModel.PreviewImage;
        mediaStore.CompleteRead("content-1");
        await Task.Delay(50);

        Assert.Same(currentPreview, viewModel.PreviewImage);
        Assert.Equal("second", viewModel.SelectedEvent?.Event.Id);
    });

    [Fact]
    public Task CaptureControls_ReusePauseBlacklistSnapshotAndAutoStartUseCase() => RunInHeadlessSession(async () =>
    {
        var coordinator = new FakeCaptureCoordinator();
        var autoStart = new FakeAutoStartService();
        using var viewModel = new CaptureControlViewModel(new CaptureControlUseCase(coordinator, autoStart));
        await viewModel.InitializeAsync();

        await viewModel.TogglePauseCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsPaused);
        Assert.Equal("恢复采集", viewModel.PauseButtonText);

        viewModel.BlacklistText = "SecretApp\nPrivate Window";
        await viewModel.SaveBlacklistCommand.ExecuteAsync(null);
        Assert.True(coordinator.Policy.IsExcluded("SecretApp", null));
        Assert.True(coordinator.Policy.IsExcluded(null, "My Private Window"));

        await viewModel.TogglePauseCommand.ExecuteAsync(null);
        await viewModel.CaptureSnapshotCommand.ExecuteAsync(null);
        await viewModel.ToggleAutoStartCommand.ExecuteAsync(null);
        Assert.False(viewModel.IsPaused);
        Assert.Equal(1, coordinator.SnapshotCount);
        Assert.True(viewModel.AutoStartEnabled);
    });

    [Fact]
    public Task Diagnostics_DisplaysRetentionLogs_AndGeneratesSanitizedPreview() => RunInHeadlessSession(async () =>
    {
        var coordinator = new FakeCaptureCoordinator();
        var retention = new FakeRetentionService();
        var logs = new FakeDiagnosticLogReader();
        var useCase = new DiagnosticsUseCase(
            new FakeCapabilityProvider(),
            new FakeMaintenanceService(),
            coordinator,
            new FakeReportJobQueue(),
            retention,
            logs);
        var viewModel = new DiagnosticsViewModel(useCase);

        await viewModel.RefreshCommand.ExecuteAsync(null);
        Assert.Contains("安全擦除过期媒体 3 个", viewModel.RetentionText);
        Assert.Single(viewModel.RecentLogs);
        Assert.False(viewModel.HasExportPreview);

        await viewModel.GeneratePreviewCommand.ExecuteAsync(null);
        Assert.True(viewModel.HasExportPreview);
        Assert.Contains("capturePipeline", viewModel.ExportPreviewJson);
    });

    [Fact]
    public Task ReportsPanel_LoadsVersionStandupAndExportsMarkdown() => RunInHeadlessSession(async () =>
    {
        var clock = new SystemClock();
        var repository = new FakeReportRepository();
        var date = DateOnly.FromDateTime(DateTime.Today);
        await repository.AppendAsync(
            date,
            "# 今日日报\n\n## 概览\n完成 S5 日报闭环。",
            """{"Standup":{"Yesterday":"完成模型网关","Today":"接入日报面板","Blockers":[]}}""",
            "qwen3:8b",
            ModelEndpointTier.LocalOffline,
            true,
            ReportTriggerSource.Manual,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        var exportDirectory = Path.Combine(AppContext.BaseDirectory, "test-temp", $"reports-{Guid.NewGuid():N}");
        var viewModel = new ReportsViewModel(new ReportUseCase(new FakeReportJobQueue(), repository, clock), exportDirectory);

        await viewModel.InitializeAsync();
        Assert.True(viewModel.HasReport);
        Assert.Single(viewModel.Versions);
        Assert.Contains("完成 S5 日报闭环", viewModel.Markdown, StringComparison.Ordinal);
        Assert.Contains("完成模型网关", viewModel.StandupText, StringComparison.Ordinal);

        await viewModel.ExportCommand.ExecuteAsync(null);
        var files = Directory.GetFiles(exportDirectory, "*.md");
        Assert.Single(files);
        Assert.Contains("S5", await File.ReadAllTextAsync(files[0]), StringComparison.Ordinal);
        Directory.Delete(exportDirectory, recursive: true);
    });

    [Fact]
    public Task VoicePanel_TogglesSessionAndReflectsListeningState() => RunInHeadlessSession(async () =>
    {
        var session = new FakeVoiceSession();
        using var viewModel = new VoiceControlViewModel(session);
        Assert.False(viewModel.IsEnabled);
        Assert.Equal("语音休眠", viewModel.StatusText);

        await viewModel.ToggleCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsEnabled);
        Assert.Equal("正在聆听", viewModel.StatusText);
        Assert.Contains("fake KWS", viewModel.CapabilityText, StringComparison.Ordinal);

        await viewModel.ToggleCommand.ExecuteAsync(null);
        Assert.False(viewModel.IsEnabled);
        Assert.Equal("语音休眠", viewModel.StatusText);
    });

    [Fact]
    public Task SearchEvidence_JumpNavigatesTimelineToCitedEvent() => RunInHeadlessSession(async () =>
    {
        var repository = new InMemoryEventRepository();
        var clock = new SystemClock();
        var timestamp = ((IClock)clock).Now();
        repository.Events.Add(CreateEvent("cited", timestamp));
        repository.Events.Add(CreateEvent("other", timestamp));

        var timeline = new TimelineViewModel(new TimelineUseCase(repository, new FakeMediaStore(), clock));
        await timeline.InitializeAsync();

        var evidence = new EvidenceReference(
            "E1",
            "cited",
            timestamp.LocalDate,
            timestamp.OccurredAtUtc,
            "TestApp",
            "Synthetic test event",
            "结构化证据摘录",
            false,
            "keyword+vector",
            0.5);
        var search = new SearchViewModel(
            new EvidenceSearchUseCase(new FakeEvidenceSearch(evidence), new FakeAnswerer(), clock),
            new RangeReportUseCase(new FakeRangeReportService(), clock),
            Path.Combine(AppContext.BaseDirectory, "test-temp", "search-ui"));
        search.Query = "迁移";
        await search.SearchCommand.ExecuteAsync(null);
        Assert.Single(search.Evidence);

        var diagnostics = new DiagnosticsUseCase(
            new FakeCapabilityProvider(),
            new FakeMaintenanceService(),
            new FakeCaptureCoordinator(),
            new FakeReportJobQueue());
        using var shell = new ShellViewModel(
            timeline,
            new CaptureControlViewModel(new CaptureControlUseCase(new FakeCaptureCoordinator(), new FakeAutoStartService())),
            new DiagnosticsViewModel(diagnostics),
            new ReportsViewModel(
                new ReportUseCase(new FakeReportJobQueue(), new FakeReportRepository(), clock),
                Path.Combine(AppContext.BaseDirectory, "test-temp", "reports-ui")),
            voice: null,
            voiceNavigation: null,
            search: search);

        shell.SelectedSectionIndex = 1;
        search.Evidence[0].NavigateCommand.Execute(null);

        await WaitUntilAsync(() => timeline.SelectedEvent?.Event.Id == "cited");
        Assert.Equal(0, shell.SelectedSectionIndex);
        Assert.Equal("cited", timeline.SelectedEvent?.Event.Id);
        Assert.Contains("已定位", timeline.StatusMessage, StringComparison.Ordinal);
    });

    [Fact]
    public Task DataManagement_RequiresTwoStepsBeforeDeleting() => RunInHeadlessSession(async () =>
    {
        var deletion = new FakeDataDeletionService();
        var viewModel = new DataManagementViewModel(deletion);

        await viewModel.DeleteDateCommand.ExecuteAsync(null);
        Assert.True(viewModel.HasPending);
        Assert.Equal(0, deletion.Calls);
        Assert.Contains("再次点击", viewModel.PendingText, StringComparison.Ordinal);
        Assert.Equal("确认删除", viewModel.DeleteDateButtonText);

        await viewModel.DeleteDateCommand.ExecuteAsync(null);
        Assert.False(viewModel.HasPending);
        Assert.Equal(1, deletion.Calls);
        Assert.Contains("已删除", viewModel.StatusMessage, StringComparison.Ordinal);
    });

    [Fact]
    public Task Shell_SectionShortcutsSwitchTabsAndRejectOutOfRange() => RunInHeadlessSession(async () =>
    {
        using var shell = CreateShell(out _);
        await shell.InitializeAsync();

        shell.SelectSectionCommand.Execute("4");
        Assert.Equal(4, shell.SelectedSectionIndex);

        shell.SelectSectionCommand.Execute("9");
        Assert.Equal(4, shell.SelectedSectionIndex);

        await shell.RefreshCurrentCommand.ExecuteAsync(null);
    });

    private static ShellViewModel CreateShell() => CreateShell(out _);

    private static ShellViewModel CreateShell(out DataManagementViewModel data)
    {
        var repository = new InMemoryEventRepository();
        var mediaStore = new FakeMediaStore();
        var clock = new SystemClock();
        var coordinator = new FakeCaptureCoordinator();
        var diagnostics = new DiagnosticsUseCase(
            new FakeCapabilityProvider(),
            new FakeMaintenanceService(),
            coordinator,
            new FakeReportJobQueue());
        data = new DataManagementViewModel(new FakeDataDeletionService());
        return new ShellViewModel(
            new TimelineViewModel(new TimelineUseCase(repository, mediaStore, clock)),
            new CaptureControlViewModel(new CaptureControlUseCase(coordinator, new FakeAutoStartService())),
            new DiagnosticsViewModel(diagnostics),
            new ReportsViewModel(
                new ReportUseCase(new FakeReportJobQueue(), new FakeReportRepository(), clock),
                Path.Combine(AppContext.BaseDirectory, "test-temp", "reports-ui")),
            voice: null,
            voiceNavigation: null,
            search: null,
            data: data);
    }

    private static async Task RunInHeadlessSession(Func<Task> test)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(DesktopTestAppBuilder));
        await session.Dispatch(test, CancellationToken.None);
    }

    private static ActivityEvent CreateEvent(string id, OccurredTimestamp timestamp, string? contentId = null) =>
        new(
            id,
            timestamp,
            SourceKind.ScreenFrame,
            "TestApp",
            "Synthetic test event",
            mediaRef: contentId is null
                ? null
                : new ContentRef(contentId, ContentKind.ScreenshotPng, new string('0', 64), 100, timestamp.OccurredAtUtc, null));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(condition());
    }

    private sealed class InMemoryEventRepository : IEventRepository
    {
        public List<ActivityEvent> Events { get; } = [];

        public Task AppendBatchAsync(IReadOnlyList<ActivityEvent> events, CancellationToken ct)
        {
            Events.AddRange(events);
            return Task.CompletedTask;
        }

        public Task AppendWithMediaBatchAsync(IReadOnlyList<EventWithMediaWrite> items, CancellationToken ct) =>
            AppendBatchAsync(items.Select(item => item.Event).ToArray(), ct);

        public async IAsyncEnumerable<ActivityEvent> QueryAsync(
            EventQuery query,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            foreach (var item in Events.Where(item => item.Timestamp.LocalDate == query.LocalDate))
            {
                ct.ThrowIfCancellationRequested();
                yield return item;
            }
        }

        public Task<ActivityEvent?> GetByIdAsync(string eventId, CancellationToken ct) =>
            Task.FromResult(Events.FirstOrDefault(item => item.Id == eventId));

        public Task<IReadOnlyList<DateOnly>> ListActiveDatesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DateOnly>>(Events.Select(item => item.Timestamp.LocalDate).Distinct().ToArray());

        public Task<bool> DeleteEventAsync(string eventId, CancellationToken ct) => Task.FromResult(false);

        public Task<int> PurgeDateAsync(DateOnly localDate, CancellationToken ct) => Task.FromResult(0);
    }

    private sealed class FakeMediaStore : IMediaStore
    {
        private static readonly byte[] Png = PngEncoder.EncodeBgra32(
        [
            49, 87, 213, 255,
            49, 87, 213, 255,
            49, 87, 213, 255,
            49, 87, 213, 255
        ], 2, 2, 8);
        private readonly Dictionary<string, TaskCompletionSource<byte[]?>> _pending = new(StringComparer.Ordinal);

        public bool DelayReads { get; init; }

        public int ReadCount { get; private set; }

        public string? LastReadContentId { get; private set; }

        public Task<ContentRef> PutAsync(ContentWriteRequest request, CancellationToken ct) => throw new NotSupportedException();

        public Task<byte[]?> ReadDecryptedAsync(string contentId, CancellationToken ct)
        {
            ReadCount++;
            LastReadContentId = contentId;
            if (!DelayReads)
            {
                return Task.FromResult<byte[]?>(Png.ToArray());
            }

            var completion = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[contentId] = completion;
            return completion.Task.WaitAsync(ct);
        }

        public void CompleteRead(string contentId) => _pending[contentId].TrySetResult(Png.ToArray());

        public Task<int> IncrementRefAsync(string contentId, CancellationToken ct) => Task.FromResult(1);

        public Task<int> DecrementRefAsync(string contentId, CancellationToken ct) => Task.FromResult(0);

        public Task<int> PurgeExpiredAsync(DateTimeOffset nowUtc, CancellationToken ct) => Task.FromResult(0);
    }

    private sealed class FakeCapabilityProvider : ICapabilityProvider
    {
        public string PlatformName => "Headless test platform";

        public IReadOnlyList<CapabilityState> GetCapabilities() =>
            [CapabilityState.Supported(CapabilityKind.SystemTray, "Headless tray test")];
    }

    private sealed class FakeMaintenanceService : IDatabaseMaintenanceService
    {
        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<DatabaseHealthReport> CheckHealthAsync(CancellationToken ct) =>
            Task.FromResult(new DatabaseHealthReport(true, SchemaVersion.Current, "test-cipher", "test-vector", 0, 0, "ok"));

        public Task BackupEncryptedAsync(string destinationPath, CancellationToken ct) => Task.CompletedTask;

        public Task RestoreEncryptedAsync(string backupSourcePath, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeReportJobQueue : IReportJobQueue
    {
        public Task<EnqueueResult> EnqueueAsync(ReportJobRequest request, CancellationToken ct) => throw new NotSupportedException();

        public Task<int> RecoverInterruptedJobsAsync(CancellationToken ct) => Task.FromResult(0);

        public Task<ReportJob?> ClaimNextPendingAsync(CancellationToken ct) => Task.FromResult<ReportJob?>(null);

        public Task UpdateAsync(ReportJob job, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<ReportJob>> ListRecentJobsAsync(int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ReportJob>>([]);
    }

    private sealed class FakeReportRepository : IReportRepository
    {
        private readonly List<DailyReport> _reports = [];

        public Task<DailyReport?> GetLatestAsync(DateOnly localDate, CancellationToken ct) =>
            Task.FromResult(_reports.Where(report => report.LocalDate == localDate)
                .OrderByDescending(report => report.Version)
                .FirstOrDefault());

        public Task<IReadOnlyList<DailyReport>> ListVersionsAsync(DateOnly localDate, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DailyReport>>(_reports.Where(report => report.LocalDate == localDate)
                .OrderByDescending(report => report.Version)
                .ToArray());

        public Task<DailyReport> AppendAsync(
            DateOnly localDate,
            string markdown,
            string statsJson,
            string modelId,
            ModelEndpointTier endpointTier,
            bool redactionApplied,
            ReportTriggerSource triggerSource,
            DateTimeOffset generatedAtUtc,
            CancellationToken ct)
        {
            var report = new DailyReport(
                $"rpt_{_reports.Count + 1}",
                localDate,
                _reports.Count + 1,
                markdown,
                statsJson,
                modelId,
                endpointTier,
                redactionApplied,
                triggerSource,
                generatedAtUtc);
            _reports.Add(report);
            return Task.FromResult(report);
        }
    }

    private sealed class FakeCaptureCoordinator : ICaptureCoordinator
    {
        public PrivacyBlacklistPolicy Policy { get; } = new();

        public bool IsPaused { get; private set; }

        public IReadOnlyList<string> BlacklistedPatterns => Policy.GetEffectivePatterns();

        public int SnapshotCount { get; private set; }

        public event EventHandler<CapturePipelineMetrics>? MetricsUpdated;

        public CapturePipelineMetrics GetMetricsSnapshot() => new(IsPaused, 0, 0, 0, 0, 0, 0);

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public Task PauseAsync(string reason, CancellationToken ct)
        {
            IsPaused = true;
            MetricsUpdated?.Invoke(this, GetMetricsSnapshot());
            return Task.CompletedTask;
        }

        public Task ResumeAsync(CancellationToken ct)
        {
            IsPaused = false;
            MetricsUpdated?.Invoke(this, GetMetricsSnapshot());
            return Task.CompletedTask;
        }

        public Task SetBlacklistedPatternsAsync(IReadOnlyList<string> patterns, CancellationToken ct)
        {
            Policy.UpdateCustomPatterns(patterns);
            return Task.CompletedTask;
        }

        public Task SubmitSampleAsync(CapturedSample sample, CancellationToken ct) => Task.CompletedTask;

        public Task CaptureImmediateSnapshotAsync(CancellationToken ct)
        {
            SnapshotCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeAutoStartService : IAutoStartService
    {
        private bool _enabled;

        public CapabilityState Capability => CapabilityState.Supported(CapabilityKind.AutoStart, "Fake");

        public Task<bool> IsEnabledAsync(CancellationToken ct) => Task.FromResult(_enabled);

        public Task SetEnabledAsync(bool enabled, CancellationToken ct)
        {
            _enabled = enabled;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeVoiceSession : IVoiceSession
    {
        public VoiceSessionState State { get; private set; } = VoiceSessionState.Sleeping;
        public CapabilityState Capability => CapabilityState.Supported(CapabilityKind.WakeWord, "fake KWS");
        public event EventHandler<VoiceSessionState>? StateChanged;
        public event EventHandler<VoiceInteraction>? Interaction;

        public Task StartAsync(CancellationToken ct)
        {
            State = VoiceSessionState.Listening;
            StateChanged?.Invoke(this, State);
            Interaction?.Invoke(this, new VoiceInteraction(State, "你好时光", string.Empty, "我在听", DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct)
        {
            State = VoiceSessionState.Sleeping;
            StateChanged?.Invoke(this, State);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeDataDeletionService : IDataDeletionService
    {
        public int Calls { get; private set; }

        public Task<DataDeletionResult> PurgeDateAsync(DateOnly localDate, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new DataDeletionResult(1, 1, 1, 0, 1));
        }

        public Task<DataDeletionResult> PurgeAllAsync(CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new DataDeletionResult(2, 2, 2, 2, 2));
        }
    }

    private sealed class FakeEvidenceSearch(EvidenceReference evidence) : IEvidenceSearch
    {
        public Task<SearchQueryResult> SearchAsync(
            string query,
            DateOnly fromDate,
            DateOnly toDate,
            CancellationToken ct) =>
            Task.FromResult(new SearchQueryResult(query, fromDate, toDate, [evidence], 1, 0));
    }

    private sealed class FakeAnswerer : IEvidenceQuestionAnswerer
    {
        public Task<EvidenceQaAnswer> AnswerAsync(
            string question,
            DateOnly fromDate,
            DateOnly toDate,
            CancellationToken ct) =>
            Task.FromResult(new EvidenceQaAnswer(
                question,
                "没有找到可验证的本地活动证据，因此不生成推测性回答。",
                [],
                [],
                "none",
                ModelEndpointTier.LocalOffline,
                true,
                "no-evidence"));
    }

    private sealed class FakeRangeReportService : IRangeReportService
    {
        public Task<RangeReport> GenerateAsync(DateOnly fromDate, DateOnly toDate, CancellationToken ct) =>
            Task.FromResult(new RangeReport(
                fromDate,
                toDate,
                "# 区间报告",
                "{}",
                [],
                "none",
                true,
                "no-evidence"));
    }

    private sealed class FakeRetentionService : IRetentionService
    {
        public MediaRetentionMetrics GetMetricsSnapshot() => new(2, 3, DateTimeOffset.UtcNow);
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<int> RunSweepNowAsync(CancellationToken ct) => Task.FromResult(0);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeDiagnosticLogReader : IDiagnosticLogReader
    {
        public IReadOnlyList<DiagnosticLogEntry> GetRecentEntries(int maxCount = 50) =>
        [
            new DiagnosticLogEntry(DateTimeOffset.UtcNow, "Information", "AppRuntime", "Runtime initialized cleanly.")
        ];

        public Task ExportConfirmedPreviewAsync(string destinationPath, string confirmedJsonPreview, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
