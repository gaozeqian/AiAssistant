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
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AiAssistant.Desktop.Tests;

[Trait("Category", "UI")]
public sealed partial class DesktopViewModelTests
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
    public Task MainWindow_ExposesAccessibleTabNamesAndAccelerators() => RunInHeadlessSession(async () =>
    {
        using var shell = CreateShell();
        var window = new MainWindow { DataContext = shell };
        window.Show();
        await shell.InitializeAsync();
        await WaitUntilAsync(() => window.GetVisualDescendants().OfType<TabItem>().Count() == 7);

        var tabs = window.GetVisualDescendants().OfType<TabItem>().ToArray();
        Assert.Equal(7, tabs.Length);
        Assert.Equal(
            new[] { "时间线", "检索与问答", "日报/周报", "隐私与控制", "语音", "数据与导出", "本地诊断" },
            tabs.Select(tab => AutomationProperties.GetName(tab)).ToArray());
        Assert.All(tabs, tab =>
        {
            var accelerator = AutomationProperties.GetAcceleratorKey(tab);
            Assert.True(accelerator?.StartsWith("Ctrl+", StringComparison.Ordinal) == true, $"missing accelerator for {tab.Header}");
        });

        var tabControl = window.GetVisualDescendants().OfType<TabControl>().Single();
        Assert.Equal("主导航", AutomationProperties.GetName(tabControl));
        window.Close();
    });

    [Fact]
    public Task MainWindow_PrimaryControlsExposeAutomationNames() => RunInHeadlessSession(async () =>
    {
        using var shell = CreateShell();
        var window = new MainWindow { DataContext = shell };
        window.Show();
        await shell.InitializeAsync();
        await WaitUntilAsync(() => Names(window).Contains("搜索应用、标题或正文"));

        Assert.Contains("搜索应用、标题或正文", Names(window));
        Assert.Contains("刷新时间线", Names(window));

        shell.SelectedSectionIndex = ShellSection.Search;
        await WaitUntilAsync(() => Names(window).Contains("基于证据提问"));
        Assert.Contains("检索关键词或问题", Names(window));
        Assert.Contains("检索证据", Names(window));
        Assert.Contains("基于证据提问", Names(window));

        shell.SelectedSectionIndex = ShellSection.Voice;
        await WaitUntilAsync(() => Names(window).Contains("记录命令音频"));
        Assert.Contains("记录命令音频", Names(window));

        shell.SelectedSectionIndex = ShellSection.Diagnostics;
        await WaitUntilAsync(() => Names(window).Contains("重新检查平台能力"));
        Assert.Contains("重新检查平台能力", Names(window));
        window.Close();
    });

    [Fact]
    public Task Window_KeyboardShortcutsSwitchTabs() => RunInHeadlessSession(async () =>
    {
        using var shell = CreateShell();
        var window = new MainWindow { DataContext = shell };
        window.Show();
        await shell.InitializeAsync();
        window.Activate();

        Assert.Equal(0, shell.SelectedSectionIndex);
        window.KeyPress(Key.D2, RawInputModifiers.Control, PhysicalKey.Digit2, "2");
        await WaitUntilAsync(() => shell.SelectedSectionIndex == 1);
        Assert.Equal(1, shell.SelectedSectionIndex);

        window.KeyPress(Key.D7, RawInputModifiers.Control, PhysicalKey.Digit7, "7");
        await WaitUntilAsync(() => shell.SelectedSectionIndex == 6);
        Assert.Equal(6, shell.SelectedSectionIndex);

        window.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.F, "f");
        await WaitUntilAsync(() => shell.SelectedSectionIndex == 1);
        Assert.Equal(1, shell.SelectedSectionIndex);
        window.Close();
    });

    private static HashSet<string> Names(Window window) =>
        window.GetVisualDescendants()
            .OfType<Control>()
            .Select(control => AutomationProperties.GetName(control))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToHashSet(StringComparer.Ordinal);

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


    [Fact]
    public Task ReportAndDataViewModels_SeedLocalTodayWithoutTimezoneCrash() => RunInHeadlessSession(async () =>
    {
        // Regression for a release-blocking bug: both view models used to seed their date with
        // `new DateTimeOffset(DateTime.Today, TimeSpan.Zero)`, which throws on any non-UTC machine
        // (DateTime.Today is Kind=Local; offset Zero only matches UTC). The exception aborted the
        // entire desktop shell, so the app showed no window and no tray on e.g. GMT+8 machines.
        var clock = new SystemClock();
        var reports = new ReportsViewModel(
            new ReportUseCase(new FakeReportJobQueue(), new FakeReportRepository(), clock),
            TempArtifactDirectory.CreatePath("reports-tz"));
        var data = new DataManagementViewModel(new FakeDataDeletionService());

        Assert.NotNull(reports.SelectedDate);
        Assert.NotNull(data.SelectedDate);
        Assert.Equal(DateTimeOffset.Now.Date, reports.SelectedDate!.Value);
        Assert.Equal(DateTimeOffset.Now.Date, data.SelectedDate!.Value);
        await Task.CompletedTask;
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
                TempArtifactDirectory.CreatePath("reports-ui")),
            voice: null,
            voiceNavigation: null,
            search: null,
            data: data);
    }

    // xunit.v3 下每次 StartNew + Dispose 会话会因 Dispatcher 停止等待而挂起；
    // 改为程序集级共享会话，生命周期由测试宿主进程统一回收。
    private static readonly HeadlessUnitTestSession SharedHeadlessSession =
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(DesktopViewModelTests).Assembly);

    /// <summary>
    /// Dispatches onto the shared headless UI thread. The <c>Func&lt;Task&lt;int&gt;&gt;</c> shape is
    /// deliberate: passing a bare <c>Func&lt;Task&gt;</c> binds to the synchronous
    /// <c>Dispatch&lt;TResult&gt;(Func&lt;TResult&gt;)</c> overload, which returns <c>Task&lt;Task&gt;</c>.
    /// Awaiting that only awaits the outer task, so the test body keeps running unobserved and its
    /// assertions never fail the run.
    /// </summary>
    private static async Task RunInHeadlessSession(Func<Task> test)
    {
        await SharedHeadlessSession.Dispatch(
            async () =>
            {
                await test().ConfigureAwait(true);
                return 0;
            },
            CancellationToken.None).ConfigureAwait(false);
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
            // Headless sessions have no running main loop, so pending layout/render jobs only
            // execute when pumped explicitly; Task.Delay alone never materializes the visual tree.
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Dispatcher.UIThread.RunJobs();
        Assert.True(condition());
    }


}
