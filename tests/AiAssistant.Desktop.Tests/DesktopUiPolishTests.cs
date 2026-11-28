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
using Avalonia.Styling;
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
        shell.SelectedSectionIndex = ShellSection.Capture; // 隐私与控制：有 primary 按钮
        await WaitUntilAsync(() => PrimaryButton(window) is not null);

        var button = PrimaryButton(window);
        Assert.NotNull(button);
        var presenter = button!.GetVisualDescendants().OfType<ContentPresenter>().First();
        Assert.Equal(Color.Parse("#0078D4"), SolidColor(presenter.Background));

        ((IPseudoClasses)button.Classes).Set(":pointerover", true);
        Dispatcher.UIThread.RunJobs();
        // 关键回归：悬停必须仍是 accent 色，而不是 Fluent 的 10% 黑白叠加。
        // 背景有 120ms BrushTransition，等它收敛到目标色。
        await WaitUntilAsync(() => SolidColor(presenter.Background) == Color.Parse("#0A6CBF"));

        ((IPseudoClasses)button.Classes).Set(":pointerover", false);
        ((IPseudoClasses)button.Classes).Set(":pressed", true);
        Dispatcher.UIThread.RunJobs();
        await WaitUntilAsync(() => SolidColor(presenter.Background) == Color.Parse("#095A9E"));

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

        // 左侧列表与右侧详情各有一份同名空态，取列表里那一份（祖辈是 DockPanel）。
        var emptyText = window.GetVisualDescendants().OfType<TextBlock>()
            .First(block => block.Text == "这一天还没有活动"
                && block.GetVisualAncestors().OfType<DockPanel>().Any());
        var emptyPanel = emptyText.GetVisualAncestors().OfType<StackPanel>().First();
        Assert.True(emptyPanel.IsVisible);

        var timestamp = ((IClock)new SystemClock()).Now();
        shell.Timeline.Events.Add(new TimelineEventItem(CreateEvent("only-row", timestamp)));
        await WaitUntilAsync(() => !emptyPanel.IsVisible);
        Assert.False(emptyPanel.IsVisible);
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
        shell.SelectedSectionIndex = ShellSection.Search;
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


    [Fact]
    public Task DesignTokens_AreRegisteredForBothThemes() => RunInHeadlessSession(() =>
    {
        Assert.Equal(12d, Avalonia.Application.Current!.Resources["FontSizeBody"]);
        Assert.Equal(11d, Avalonia.Application.Current.Resources["FontSizeCaption"]);
        Assert.Equal(15d, Avalonia.Application.Current.Resources["FontSizeTitle"]);
        Assert.Equal(20d, Avalonia.Application.Current.Resources["FontSizePage"]);
        Assert.Equal(new Thickness(24), Avalonia.Application.Current.Resources["SpaceXl"]);
        Assert.Equal(new CornerRadius(10), Avalonia.Application.Current.Resources["RadiusMd"]);
        Assert.IsType<TimeSpan>(Avalonia.Application.Current.Resources["DurationBase"]);
        return Task.CompletedTask;
    });

    [Fact]
    public void DesignTokens_ReducedMotionZeroesDurations()
    {
        var resources = new ResourceDictionary();
        DesignTokens.AddTo(resources, reducedMotion: true);
        Assert.Equal(TimeSpan.Zero, resources["DurationFast"]);
        Assert.Equal(TimeSpan.Zero, resources["DurationBase"]);
        Assert.Equal(TimeSpan.Zero, resources["DurationSlow"]);
    }

    [Fact]
    public Task AccentTextBrush_MeetsAaOnCardBackground() => RunInHeadlessSession(() =>
    {
        foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            var foreground = Resolve<ISolidColorBrush>("AccentTextBrush", variant);
            var background = Resolve<ISolidColorBrush>("CardBackgroundBrush", variant);
            var ratio = Contrast(foreground.Color, background.Color);
            Assert.True(ratio >= 4.5, $"{variant}: accent text contrast {ratio:0.00} < 4.5");
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task DangerButton_PointerOverUsesDangerBackground() => RunInHeadlessSession(async () =>
    {
        using var shell = CreateShell();
        var window = new MainWindow { DataContext = shell };
        window.Show();
        await shell.InitializeAsync();
        shell.SelectedSectionIndex = ShellSection.Data;
        await WaitUntilAsync(() => DangerButton(window) is not null);

        var button = DangerButton(window);
        Assert.NotNull(button);
        var presenter = button!.GetVisualDescendants().OfType<ContentPresenter>().First();
        ((IPseudoClasses)button.Classes).Set(":pointerover", true);
        Dispatcher.UIThread.RunJobs();

        var expected = Resolve<ISolidColorBrush>("DangerBackgroundBrush", ThemeVariant.Light).Color;
        await WaitUntilAsync(() => SolidColor(presenter.Background) == expected);
        ((IPseudoClasses)button.Classes).Set(":pointerover", false);
        Dispatcher.UIThread.RunJobs();
        window.Close();
    });

    [Fact]
    public Task EvidenceList_ShowsEmptyStateUntilResultsArrive() => RunInHeadlessSession(async () =>
    {
        var clock = new SystemClock();
        var timeline = new TimelineViewModel(
            new TimelineUseCase(new InMemoryEventRepository(), new FakeMediaStore(), clock));
        var timestamp = ((IClock)clock).Now();
        var evidence = new EvidenceReference(
            "E1", "cited", timestamp.LocalDate, timestamp.OccurredAtUtc,
            "TestApp", "Synthetic test event", "结构化证据摘录", false, "keyword+vector", 0.5);
        var search = new SearchViewModel(
            new EvidenceSearchUseCase(new FakeEvidenceSearch(evidence), new FakeAnswerer(), clock),
            TempArtifactDirectory.CreatePath("search-empty"));
        using var shell = new ShellViewModel(
            timeline,
            new CaptureControlViewModel(new CaptureControlUseCase(new FakeCaptureCoordinator(), new FakeAutoStartService())),
            new DiagnosticsViewModel(new DiagnosticsUseCase(
                new FakeCapabilityProvider(), new FakeMaintenanceService(),
                new FakeCaptureCoordinator(), new FakeReportJobQueue())),
            new ReportsViewModel(
                new ReportUseCase(new FakeReportJobQueue(), new FakeReportRepository(), clock),
                TempArtifactDirectory.CreatePath("reports-empty")),
            voice: null, voiceNavigation: null, search: search);

        var window = new MainWindow { DataContext = shell };
        window.Show();
        await shell.InitializeAsync();
        shell.SelectedSectionIndex = ShellSection.Search;
        await WaitUntilAsync(() => EmptyEvidenceHint(window) is not null);

        var hint = EmptyEvidenceHint(window);
        Assert.NotNull(hint);
        Assert.True(hint!.IsVisible);

        search.Query = "迁移";
        await search.SearchCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => !hint.IsVisible);
        Assert.False(hint.IsVisible);
        window.Close();
    });

    [Fact]
    public Task Reports_WeeklySwitchTogglesContentAndTitle() => RunInHeadlessSession(async () =>
    {
        using var shell = CreateShell();
        var window = new MainWindow { DataContext = shell };
        window.Show();
        await shell.InitializeAsync();
        shell.SelectedSectionIndex = ShellSection.Reports;
        await WaitUntilAsync(() => shell.Reports.ShowDailyView);

        Assert.Equal("日报正文", shell.Reports.ContentTitle);
        shell.Reports.ShowWeeklyCommand.Execute(null);
        Assert.True(shell.Reports.IsWeeklyView);
        Assert.False(shell.Reports.ShowDailyView);
        Assert.Equal("周报正文", shell.Reports.ContentTitle);

        shell.Reports.ShowDailyCommand.Execute(null);
        Assert.False(shell.Reports.IsWeeklyView);
        Assert.Equal("日报正文", shell.Reports.ContentTitle);
        window.Close();
        await Task.CompletedTask;
    });

    [Fact]
    public Task Reports_WeeklyReportIsGeneratedInsideReportsTab() => RunInHeadlessSession(async () =>
    {
        var clock = new SystemClock();
        using var reports = new ReportsViewModel(
            new ReportUseCase(new FakeReportJobQueue(), new FakeReportRepository(), clock),
            TempArtifactDirectory.CreatePath("reports-weekly"),
            clock,
            range: new RangeReportUseCase(new FakeRangeReportService(), clock));

        Assert.True(reports.WeeklyReportCommand.CanExecute(null));
        await reports.WeeklyReportCommand.ExecuteAsync(null);
        Assert.Equal("# 区间报告", reports.WeeklyMarkdown);
        Assert.True(reports.HasWeeklyReport);
        Assert.True(reports.IsWeeklyView);
        Assert.True(reports.ExportWeeklyCommand.CanExecute(null));
        await Task.CompletedTask;
    });

    private static Button? DangerButton(Window window) =>
        window.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(button => button.Classes.Contains("danger") && button.IsEffectivelyVisible);

    private static TextBlock? EmptyEvidenceHint(Window window) =>
        window.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(block => block.Text?.StartsWith("暂无证据", StringComparison.Ordinal) == true);

    private static T Resolve<T>(string key, ThemeVariant variant) where T : class
    {
        Assert.True(Avalonia.Application.Current!.TryFindResource(key, variant, out var value), $"missing resource {key}");
        return Assert.IsAssignableFrom<T>(value);
    }

    private static double Contrast(Color first, Color second)
    {
        var a = Luminance(first);
        var b = Luminance(second);
        var (high, low) = a >= b ? (a, b) : (b, a);
        return (high + 0.05) / (low + 0.05);
    }

    private static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            var c = value / 255d;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));
    }

    private static Button? PrimaryButton(Window window) =>
        window.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(button => button.Classes.Contains("primary") && button.IsEffectivelyVisible);

    private static TextBlock? AnswerBlock(Window window, string answer) =>
        window.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(block => block.Text == answer && block.IsEffectivelyVisible);

    private static Color SolidColor(IBrush? brush) =>
        Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;
}
