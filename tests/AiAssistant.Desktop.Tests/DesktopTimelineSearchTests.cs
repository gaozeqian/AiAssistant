using System.Globalization;
using System.Runtime.CompilerServices;
using AiAssistant.Application.Policies;
using AiAssistant.Application.Ports;
using AiAssistant.Application.UseCases;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Desktop.ViewModels;
using AiAssistant.Desktop.Views;
using AiAssistant.Testing.Common;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace AiAssistant.Desktop.Tests;

[Trait("Category", "UI")]
public sealed partial class DesktopViewModelTests
{
    [Fact]
    public Task Timeline_DecryptsMediaOnlyAfterEventSelection() => RunInHeadlessSession(async () =>
    {
        var repository = new InMemoryEventRepository();
        var mediaStore = new FakeMediaStore();
        var clock = new SystemClock();
        // Midday keeps both events on the same local date regardless of when the test runs.
        var now = ((IClock)clock).Now();
        var noonLocal = now.LocalDate.ToDateTime(new TimeOnly(12, 0));
        var noon = OccurredTimestamp.FromInstant(
            new DateTimeOffset(noonLocal, TimeZoneInfo.Local.GetUtcOffset(noonLocal)),
            TimeZoneInfo.Local);
        repository.Events.Add(CreateEvent("without-media", noon));
        using var viewModel = new TimelineViewModel(new TimelineUseCase(repository, mediaStore, clock));

        await viewModel.InitializeAsync();
        Assert.Single(viewModel.Events);
        Assert.Equal(0, mediaStore.ReadCount);

        repository.Events.Add(CreateEvent(
            "with-media",
            OccurredTimestamp.FromInstant(noon.OccurredAtUtc.AddSeconds(1), TimeZoneInfo.Local),
            "content-1"));
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
    public Task Timeline_ReselectAfterRefresh_ReloadsPreview() => RunInHeadlessSession(async () =>
    {
        var repository = new InMemoryEventRepository();
        var mediaStore = new FakeMediaStore();
        var clock = new SystemClock();
        var timestamp = ((IClock)clock).Now();
        repository.Events.Add(CreateEvent("first", timestamp, "content-1"));
        using var viewModel = new TimelineViewModel(new TimelineUseCase(repository, mediaStore, clock));

        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.PreviewImage is not null);

        // Refresh clears Events; the bound ListBox answers by pushing SelectedItem=null back,
        // which used to dispose the media CTS without clearing it.
        await viewModel.RefreshCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => viewModel.PreviewImage is not null);

        viewModel.SelectedEvent = null;
        Assert.False(viewModel.HasPreview);

        viewModel.SelectedEvent = viewModel.Events[0];
        await WaitUntilAsync(() => viewModel.HasPreview);
    });

    [Fact]
    public Task Timeline_PagesNewestFirst_AndLoadsAllRecordsBeyondFormerCap() => RunInHeadlessSession(async () =>
    {
        var repository = new InMemoryEventRepository();
        var clock = new SystemClock();
        var now = ((IClock)clock).Now();
        var startOfDayLocal = now.LocalDate.ToDateTime(TimeOnly.MinValue);
        var timeZone = TimeZoneInfo.Local;
        var offset = timeZone.GetUtcOffset(startOfDayLocal);
        const int total = 1200; // more than the old hard-coded 1000-row cap
        for (var i = 0; i < total; i++)
        {
            var instant = new DateTimeOffset(startOfDayLocal.AddMinutes(i), offset);
            repository.Events.Add(CreateEvent(
                "e" + i.ToString("D4", CultureInfo.InvariantCulture),
                OccurredTimestamp.FromInstant(instant, timeZone)));
        }

        using var viewModel = new TimelineViewModel(new TimelineUseCase(repository, new FakeMediaStore(), clock));
        await viewModel.InitializeAsync();

        // First page is the newest slice, rendered newest→oldest: the top row is the latest event.
        Assert.Equal(TimelineUseCase.DefaultPageSize, viewModel.Events.Count);
        Assert.Equal("e1199", viewModel.Events[0].Event.Id);
        Assert.Equal("e1000", viewModel.Events[^1].Event.Id);
        Assert.Equal("e1199", viewModel.SelectedEvent?.Event.Id);
        Assert.True(viewModel.HasMore);

        while (viewModel.HasMore)
        {
            await viewModel.LoadMoreCommand.ExecuteAsync(null);
        }

        Assert.Equal(total, viewModel.Events.Count);
        Assert.Equal("e1199", viewModel.Events[0].Event.Id);
        Assert.Equal("e0000", viewModel.Events[^1].Event.Id);
        Assert.Equal(total, viewModel.Events.Select(item => item.Event.Id).Distinct().Count());
        Assert.Contains("已加载 1200 条", viewModel.StatusMessage, StringComparison.Ordinal);
    });

    [Fact]
    public Task Timeline_NewRecordsDuringPaging_DoNotDuplicateOrSkipRows() => RunInHeadlessSession(async () =>
    {
        var repository = new InMemoryEventRepository();
        var clock = new SystemClock();
        var now = ((IClock)clock).Now();
        var startOfDayLocal = now.LocalDate.ToDateTime(TimeOnly.MinValue);
        var timeZone = TimeZoneInfo.Local;
        var offset = timeZone.GetUtcOffset(startOfDayLocal);
        const int total = 300;
        for (var i = 0; i < total; i++)
        {
            var instant = new DateTimeOffset(startOfDayLocal.AddMinutes(i), offset);
            repository.Events.Add(CreateEvent(
                "e" + i.ToString("D4", CultureInfo.InvariantCulture),
                OccurredTimestamp.FromInstant(instant, timeZone)));
        }

        using var viewModel = new TimelineViewModel(new TimelineUseCase(repository, new FakeMediaStore(), clock));
        await viewModel.InitializeAsync();
        Assert.Equal(TimelineUseCase.DefaultPageSize, viewModel.Events.Count);
        Assert.Equal("e0299", viewModel.Events[0].Event.Id);

        // A capture lands while the user is paging. Offset paging would shift the next page and
        // skip/duplicate a row; the keyset cursor must keep returning exactly the older rows.
        var lateInstant = new DateTimeOffset(startOfDayLocal.AddMinutes(total + 5), offset);
        repository.Events.Add(CreateEvent(
            "e0300",
            OccurredTimestamp.FromInstant(lateInstant, timeZone)));

        await viewModel.LoadMoreCommand.ExecuteAsync(null);

        Assert.Equal(total, viewModel.Events.Count);
        Assert.DoesNotContain("e0300", viewModel.Events.Select(item => item.Event.Id));
        Assert.Equal("e0000", viewModel.Events[^1].Event.Id);
        Assert.Equal(total, viewModel.Events.Select(item => item.Event.Id).Distinct().Count());
        Assert.Equal("e0299", viewModel.Events[0].Event.Id);

        // A refresh is what surfaces the new row, at the top.
        await viewModel.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("e0300", viewModel.Events[0].Event.Id);
        Assert.Equal(TimelineUseCase.DefaultPageSize, viewModel.Events.Count);
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
            TempArtifactDirectory.CreatePath("search-ui"));
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
                TempArtifactDirectory.CreatePath("reports-ui")),
            voice: null,
            voiceNavigation: null,
            search: search);

        shell.SelectedSectionIndex = ShellSection.Search;
        search.Evidence[0].NavigateCommand.Execute(null);

        await WaitUntilAsync(() => timeline.SelectedEvent?.Event.Id == "cited");
        Assert.Equal(0, shell.SelectedSectionIndex);
        Assert.Equal("cited", timeline.SelectedEvent?.Event.Id);
        Assert.Contains("已定位", timeline.StatusMessage, StringComparison.Ordinal);
    });


    [Fact]
    public Task FullWindow_UserSearchesAndJumpsBackToTimeline() => RunInHeadlessSession(async () =>
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
            TempArtifactDirectory.CreatePath("search-ui"));

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
                TempArtifactDirectory.CreatePath("reports-ui")),
            voice: null,
            voiceNavigation: null,
            search: search);
        var window = new MainWindow { DataContext = shell };
        window.Show();
        await shell.InitializeAsync();

        // Ctrl+F 聚焦检索页
        window.Activate();
        window.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.F, "f");
        await WaitUntilAsync(() => shell.SelectedSectionIndex == 1);
        // 切页后的控件在 UI 线程上延迟实现；先等待再访问，避免异步命令里出现未观察异常。
        await WaitUntilAsync(() => HasTextBox(window, "检索关键词或问题"));

        TextBoxByName(window, "检索关键词或问题").Text = "迁移";
        await WaitUntilAsync(() => HasButton(window, "检索证据"));
        Click(window, "检索证据");
        await WaitUntilAsync(() => search.Evidence.Count == 1);

        await WaitUntilAsync(() => HasButton(window, "定位"));
        Click(window, "定位");
        await WaitUntilAsync(() => timeline.SelectedEvent?.Event.Id == "cited");
        Assert.Equal(0, shell.SelectedSectionIndex);
        Assert.Contains("已定位", timeline.StatusMessage, StringComparison.Ordinal);
        window.Close();
    });


}
