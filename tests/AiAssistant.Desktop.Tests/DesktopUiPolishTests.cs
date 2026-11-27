using AiAssistant.Application.Ports;
using AiAssistant.Application.UseCases;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Desktop.ViewModels;
using AiAssistant.Desktop.Views;
using AiAssistant.Testing.Common;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AiAssistant.Desktop.Tests;

/// <summary>
/// Guards the batch-1 UI fixes: primary-button states, the timeline empty state, the scrollable
/// answer pane, the two-step exit and the determinate progress curve.
/// </summary>
[Trait("Category", "UI")]
public sealed partial class DesktopViewModelTests
{
    [Fact]
    public Task PrimaryButton_PointerOverPaintsAccentHoverBrush() => RunInHeadlessSession(async () =>
    {
        using var shell = CreateShell();
        var window = new MainWindow { DataContext = shell };
        window.Show();
        await shell.InitializeAsync();
        shell.SelectedSectionIndex = 2; // 隐私与控制：有 primary 按钮
        await WaitUntilAsync(() => PrimaryButton(window) is not null);

        var button = PrimaryButton(window);
        Assert.NotNull(button);
        var presenter = button!.GetVisualDescendants().OfType<ContentPresenter>().First();
        Assert.Equal(Color.Parse("#0078D4"), SolidColor(presenter.Background));

        ((IPseudoClasses)button.Classes).Set(":pointerover", true);
        Dispatcher.UIThread.RunJobs();
        // 关键回归：悬停必须仍是 accent 色，而不是 Fluent 的 10% 黑白叠加。
        Assert.Equal(Color.Parse("#0A6CBF"), SolidColor(presenter.Background));

        ((IPseudoClasses)button.Classes).Set(":pointerover", false);
        ((IPseudoClasses)button.Classes).Set(":pressed", true);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Color.Parse("#095A9E"), SolidColor(presenter.Background));

        ((IPseudoClasses)button.Classes).Set(":pressed", false);
        Dispatcher.UIThread.RunJobs();
        window.Close();
    });

    [Fact]
    public Task Timeline_EmptyStateHiddenWhenEventsExist() => RunInHeadlessSession(async () =>
    {
        using var shell = CreateShell();
        var window = new MainWindow { DataContext = shell };
        window.Show();
        await shell.InitializeAsync();

        var empty = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(block => block.Text == "暂无可显示的活动");
        Assert.True(empty.IsVisible);

        var timestamp = ((IClock)new SystemClock()).Now();
        shell.Timeline.Events.Add(new TimelineEventItem(CreateEvent("only-row", timestamp)));
        await WaitUntilAsync(() => !empty.IsVisible);
        Assert.False(empty.IsVisible);
        window.Close();
    });

    [Fact]
    public Task Search_AnswerIsHostedInScrollViewer() => RunInHeadlessSession(async () =>
    {
        var clock = new SystemClock();
        var timeline = new TimelineViewModel(
            new TimelineUseCase(new InMemoryEventRepository(), new FakeMediaStore(), clock));
        var timestamp = ((IClock)clock).Now();
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
            TempArtifactDirectory.CreatePath("search-scroll"));
        using var shell = new ShellViewModel(
            timeline,
            new CaptureControlViewModel(new CaptureControlUseCase(new FakeCaptureCoordinator(), new FakeAutoStartService())),
            new DiagnosticsViewModel(new DiagnosticsUseCase(
                new FakeCapabilityProvider(),
                new FakeMaintenanceService(),
                new FakeCaptureCoordinator(),
                new FakeReportJobQueue())),
            new ReportsViewModel(
                new ReportUseCase(new FakeReportJobQueue(), new FakeReportRepository(), clock),
                TempArtifactDirectory.CreatePath("reports-scroll")),
            voice: null,
            voiceNavigation: null,
            search: search);

        var window = new MainWindow { DataContext = shell };
        window.Show();
        await shell.InitializeAsync();
        shell.SelectedSectionIndex = 1;
        await WaitUntilAsync(() => AnswerBlock(window, search.Answer) is not null);

        var answer = AnswerBlock(window, search.Answer);
        Assert.NotNull(answer);
        // 长回答在最小窗口下必须能滚：祖辈里要有 ScrollViewer。
        Assert.NotNull(answer!.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault());
        window.Close();
    });

    [Fact]
    public Task Exit_RequiresSecondClick() => RunInHeadlessSession(async () =>
    {
        using var shell = CreateShell();
        var raised = 0;
        shell.ExitRequested += (_, _) => raised++;

        shell.RequestExitCommand.Execute(null);
        Assert.False(shell.IsExitRequested);
        Assert.Equal(0, raised);
        Assert.True(shell.IsExitArmed);
        Assert.Equal("确认退出", shell.ExitButtonText);

        shell.RequestExitCommand.Execute(null);
        Assert.True(shell.IsExitRequested);
        Assert.Equal(1, raised);
        Assert.False(shell.IsExitArmed);
        await Task.CompletedTask;
    });

    [Fact]
    public void ProgressPulse_IsMonotonicAndCapped()
    {
        var value = 0d;
        var previous = -1d;
        for (var i = 0; i < 500; i++)
        {
            value = ProgressPulseController.NextValue(value);
            Assert.True(value >= previous, $"progress went backwards at step {i}: {previous} -> {value}");
            Assert.True(value <= 95, $"progress exceeded the 95% ceiling: {value}");
            previous = value;
        }

        Assert.True(value >= 94.9, $"progress never approached the ceiling: {value}");
    }

    private static Button? PrimaryButton(Window window) =>
        window.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(button => button.Classes.Contains("primary") && button.IsEffectivelyVisible);

    private static TextBlock? AnswerBlock(Window window, string answer) =>
        window.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(block => block.Text == answer && block.IsEffectivelyVisible);

    private static Color SolidColor(IBrush? brush) =>
        Assert.IsType<SolidColorBrush>(brush).Color;
}
