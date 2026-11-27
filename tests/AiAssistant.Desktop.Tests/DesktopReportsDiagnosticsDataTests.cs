using System.Runtime.CompilerServices;
using AiAssistant.Application.Policies;
using AiAssistant.Application.Ports;
using AiAssistant.Application.UseCases;
using AiAssistant.Core.Domain;
using AiAssistant.Core.Exceptions;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Desktop.ViewModels;
using AiAssistant.Desktop.Views;
using AiAssistant.Testing.Common;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AiAssistant.Desktop.Tests;

[Trait("Category", "UI")]
public sealed partial class DesktopViewModelTests
{
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
        Assert.Contains("安全擦除过期或超限媒体 3 个", viewModel.RetentionText);
        Assert.Single(viewModel.RecentLogs);
        Assert.False(viewModel.HasExportPreview);

        await viewModel.GeneratePreviewCommand.ExecuteAsync(null);
        Assert.True(viewModel.HasExportPreview);
        Assert.Contains("capturePipeline", viewModel.ExportPreviewJson);
    });

    [Fact]
    public void CapabilityActions_MatchRemediationInsteadOfAlwaysOpeningSettings()
    {
        var permission = CapabilityActionResolver.Resolve(
            CapabilityState.PermissionRequired(CapabilityKind.ScreenCapture, "需要权限", "请授予屏幕录制。"));
        Assert.NotNull(permission);
        Assert.Equal("去授权", permission!.Label);
        Assert.Equal(CapabilityActionKind.OpenPermissionSettings, permission.Kind);
        // Linux has no cross-desktop deep link, so the button is intentionally hidden there.
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            Assert.NotNull(CapabilityActionResolver.SettingsUri(permission.PermissionTarget));
            Assert.True(CapabilityActionResolver.IsAvailable(permission));
        }
        else
        {
            Assert.Null(CapabilityActionResolver.SettingsUri(permission.PermissionTarget));
            Assert.False(CapabilityActionResolver.IsAvailable(permission));
        }

        var voice = CapabilityActionResolver.Resolve(
            CapabilityState.Degraded(CapabilityKind.WakeWord, "语音可选", "请安装模型。"));
        Assert.NotNull(voice);
        Assert.Equal("前往语音", voice!.Label);
        Assert.Equal(CapabilityActionKind.OpenVoicePage, voice.Kind);

        // No remediation hint means there is nothing actionable; an unconditional settings button was wrong.
        Assert.Null(CapabilityActionResolver.Resolve(
            CapabilityState.Degraded(CapabilityKind.Ocr, "按需兜底")));

        // OCR assets are installed outside the app, so the only useful follow-up is a fresh probe.
        var ocr = CapabilityActionResolver.Resolve(
            CapabilityState.Degraded(CapabilityKind.Ocr, "OCR 模型资产未安装", "安装后点击「重新检查」。"));
        Assert.Equal(CapabilityActionKind.RetryDiagnostics, ocr!.Kind);
        Assert.Equal("重新检查", ocr.Label);
        Assert.Equal(
            CapabilityActionKind.RetryDiagnostics,
            CapabilityActionResolver.Resolve(
                CapabilityState.PermissionRequired(CapabilityKind.Ocr, "OCR 模型资产未安装", "安装后点击「重新检查」。"))!.Kind);

        // A failed microphone is retried by re-arming voice, not by probing the same broken device.
        var audio = CapabilityActionResolver.Resolve(
            CapabilityState.Unavailable(CapabilityKind.AudioCapture, "采集不可用", "请在「语音」页重新启用。"));
        Assert.Equal(CapabilityActionKind.OpenVoicePage, audio!.Kind);

        var keyStore = CapabilityActionResolver.Resolve(
            CapabilityState.PermissionRequired(CapabilityKind.KeyStore, "已锁定", "解锁后重试。"));
        Assert.Equal(CapabilityActionKind.RetryDiagnostics, keyStore!.Kind);

        // Voice models can be ready while the session is off: the hint must survive as a real action.
        var disabledVoice = new CapabilityState(
            CapabilityKind.WakeWord,
            CapabilityStatus.Supported,
            "唤醒词与语音命令可用，语音未启用",
            "在「语音」页启用后才会监听唤醒词。");
        var voiceAction = CapabilityActionResolver.Resolve(disabledVoice);
        Assert.Equal(CapabilityActionKind.OpenVoicePage, voiceAction!.Kind);
        Assert.Equal("前往语音", voiceAction.Label);
        Assert.Equal("在「语音」页启用后才会监听唤醒词。", new CapabilityItem(disabledVoice).RemediationHint);
    }

    [Fact]
    public void CapabilityItems_NeverClaimNothingToDoWhileAttentionIsNeeded()
    {
        var supported = new CapabilityItem(CapabilityState.Supported(CapabilityKind.SystemTray, "已启用"));
        Assert.Equal("无需额外操作", supported.RemediationHint);
        Assert.False(supported.NeedsAttention);

        var degraded = new CapabilityItem(CapabilityState.Degraded(CapabilityKind.Ocr, "模型缺失"));
        Assert.Equal("暂无可执行操作", degraded.RemediationHint);
        Assert.True(degraded.NeedsAttention);
    }

    [Fact]
    public async Task Diagnostics_ExportsExactlyThePreviewTheUserConfirmed()
    {
        var logs = new RecordingDiagnosticLogReader();
        var useCase = new DiagnosticsUseCase(
            new FakeCapabilityProvider(),
            new FakeMaintenanceService(),
            new FakeCaptureCoordinator(),
            new FakeReportJobQueue(),
            logReader: logs);
        var viewModel = new DiagnosticsViewModel(
            useCase,
            exportDirectory: TempArtifactDirectory.CreatePath("diag-export"));

        Assert.False(viewModel.CanExportPreview);
        await viewModel.ExportPreviewCommand.ExecuteAsync(null);
        Assert.Null(logs.ConfirmedJson);

        await viewModel.GeneratePreviewCommand.ExecuteAsync(null);
        Assert.True(viewModel.CanExportPreview);
        var preview = viewModel.ExportPreviewJson;
        Assert.Contains("预览已生成", viewModel.ExportStatusText, StringComparison.Ordinal);

        await viewModel.ExportPreviewCommand.ExecuteAsync(null);
        Assert.Equal(preview, logs.ConfirmedJson);
    }

    [Fact]
    public async Task Diagnostics_RecentJobsDisplayIsCappedButExportKeepsTheFullTail()
    {
        var queue = new TwentyJobsQueue();
        var useCase = new DiagnosticsUseCase(
            new FakeCapabilityProvider(),
            new FakeMaintenanceService(),
            new FakeCaptureCoordinator(),
            queue);
        var viewModel = new DiagnosticsViewModel(useCase);

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(DiagnosticsViewModel.RecentJobDisplayLimit, viewModel.RecentJobs.Count);
        Assert.Contains("共 20 条", viewModel.RecentJobsSummary, StringComparison.Ordinal);
        Assert.Equal(20, queue.LastRequestedLimit);
    }

    [Fact]
    public Task DiagnosticsLayout_ExportCardIsAdjacentAndActionLabelsAreNotClipped() => RunInHeadlessSession(async () =>
    {
        var clock = new SystemClock();
        var coordinator = new FakeCaptureCoordinator();
        var diagnostics = new DiagnosticsViewModel(
            new DiagnosticsUseCase(
                new ActionCapabilityProvider(),
                new FakeMaintenanceService(),
                coordinator,
                new TwentyJobsQueue()),
            mcp: new ConfiguredMcpControl(),
            modelConfiguration: new ConfiguredModelsAdmin());
        var shell = new ShellViewModel(
            new TimelineViewModel(new TimelineUseCase(new InMemoryEventRepository(), new FakeMediaStore(), clock)),
            new CaptureControlViewModel(new CaptureControlUseCase(coordinator, new FakeAutoStartService())),
            diagnostics,
            new ReportsViewModel(
                new ReportUseCase(new FakeReportJobQueue(), new FakeReportRepository(), clock),
                TempArtifactDirectory.CreatePath("diag-layout")),
            data: new DataManagementViewModel(new FakeDataDeletionService()));
        var window = new MainWindow { DataContext = shell };
        window.Show();
        await shell.InitializeAsync();
        shell.SelectedSectionIndex = 5;
        // 前往语音 is available on every OS; 去授权 is hidden on Linux where no deep link exists.
        await WaitUntilAsync(() => window.GetVisualDescendants().OfType<Button>()
            .Any(button => button.IsVisible && button.Content?.ToString() == "前往语音"));
        await diagnostics.GeneratePreviewCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        var buttons = window.GetVisualDescendants().OfType<Button>().ToList();
        var generate = buttons.First(button => button.Content?.ToString() == "生成预览");
        var cardTitle = window.GetVisualDescendants().OfType<TextBlock>()
            .First(block => block.Text?.StartsWith("诊断导出（脱敏）", StringComparison.Ordinal) == true);
        var gap = Math.Abs(
            cardTitle.TransformToVisual(window)!.Value.Transform(new Point(0, 0)).Y
            - generate.TransformToVisual(window)!.Value.Transform(new Point(0, 0)).Y);
        Assert.InRange(gap, 0, 300);

        var actionButtons = buttons
            .Where(button => button.IsVisible && button.Content?.ToString() is "去授权" or "前往语音")
            .ToList();
        Assert.NotEmpty(actionButtons);
        foreach (var button in actionButtons)
        {
            var presenter = button.GetVisualDescendants().OfType<ContentPresenter>().FirstOrDefault();
            Assert.NotNull(presenter);
            Assert.True(
                button.Bounds.Width >= presenter!.DesiredSize.Width - 1,
                $"操作按钮文字被裁剪：{button.Content} / {button.Bounds.Width} vs {presenter.DesiredSize.Width}");
        }

        Assert.Equal(DiagnosticsViewModel.RecentJobDisplayLimit, diagnostics.RecentJobs.Count);
        window.Close();
    });

    [Fact]
    public Task DiagnosticsHeader_RefreshButtonStaysClearOfVerticalScrollBar() => RunInHeadlessSession(async () =>
    {
        using var shell = CreateShell();
        var window = new MainWindow { DataContext = shell };
        window.Show();
        await shell.InitializeAsync();
        shell.SelectedSectionIndex = 5;
        await WaitUntilAsync(() => HasButton(window, "重新检查"));

        var refresh = ButtonByText(window, "重新检查");
        var scrollViewer = refresh.GetVisualAncestors().OfType<ScrollViewer>().First();
        var scrollBar = scrollViewer.GetVisualDescendants().OfType<ScrollBar>()
            .FirstOrDefault(bar => bar.Orientation == Orientation.Vertical
                && bar.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault() == scrollViewer);
        Assert.NotNull(scrollBar);
        Assert.True(scrollBar!.IsVisible && scrollBar.Bounds.Width > 0, "诊断页应出现垂直滚动条");

        var buttonRight = refresh.TransformToVisual(window)!.Value.Transform(new Point(refresh.Bounds.Width, 0)).X;
        var barLeft = scrollBar.TransformToVisual(window)!.Value.Transform(new Point(0, 0)).X;
        Assert.True(
            buttonRight <= barLeft - 1,
            $"「重新检查」按钮右缘 {buttonRight:F1} 应位于滚动条左缘 {barLeft:F1} 左侧");
        window.Close();
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
            "gemma4:12b",
            ModelEndpointTier.LocalOffline,
            true,
            ReportTriggerSource.Manual,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        var exportDirectory = TempArtifactDirectory.CreatePath("reports");
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
    public Task ReportsPanel_ReadsPersistedLowercaseStandupFields() => RunInHeadlessSession(async () =>
    {
        var clock = new SystemClock();
        var repository = new FakeReportRepository();
        var date = DateOnly.FromDateTime(DateTime.Today);
        // Production stats JSON is serialized from DailyReportStats, whose ReportStandup members
        // carry lowercase [JsonPropertyName] attributes because the same type is the model schema.
        await repository.AppendAsync(
            date,
            "# 今日日报",
            """
            {"LocalDate":"2026-11-27","EventCount":12,"SegmentCount":3,"ActiveMinutes":95,
             "AppCounts":{"Rider":12},
             "Standup":{"yesterday":"完成模型网关","today":"接入日报面板","blockers":["等待设计评审"]},
             "ModelId":"gemma4:12b","EndpointTier":"LocalOffline","RedactionApplied":false}
            """,
            "gemma4:12b",
            ModelEndpointTier.LocalOffline,
            false,
            ReportTriggerSource.Manual,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        var viewModel = new ReportsViewModel(
            new ReportUseCase(new FakeReportJobQueue(), repository, clock),
            TempArtifactDirectory.CreatePath("reports-standup-lowercase"));

        await viewModel.InitializeAsync();

        Assert.Contains("完成模型网关", viewModel.StandupText, StringComparison.Ordinal);
        Assert.Contains("接入日报面板", viewModel.StandupText, StringComparison.Ordinal);
        Assert.Contains("等待设计评审", viewModel.StandupText, StringComparison.Ordinal);
    });

    [Fact]
    public Task ReportsPanel_ReportsEmptyStandupInsteadOfBlankLines() => RunInHeadlessSession(async () =>
    {
        var repository = new FakeReportRepository();
        await repository.AppendAsync(
            DateOnly.FromDateTime(DateTime.Today),
            "# 今日日报",
            """{"Standup":{"yesterday":"","today":"","blockers":[]}}""",
            "gemma4:12b",
            ModelEndpointTier.LocalOffline,
            false,
            ReportTriggerSource.Manual,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        var viewModel = new ReportsViewModel(
            new ReportUseCase(new FakeReportJobQueue(), repository, new SystemClock()),
            TempArtifactDirectory.CreatePath("reports-standup-empty"));

        await viewModel.InitializeAsync();

        Assert.Equal("该版本没有站会内容", viewModel.StandupText);
    });

    [Fact]
    public Task ReportsPanel_LoadsAndSavesAutomaticSchedule() => RunInHeadlessSession(async () =>
    {
        var store = new RecordingScheduleSettingsStore(
            new ReportScheduleSettings(new TimeOnly(0, 30), Enabled: true));
        var viewModel = new ReportsViewModel(
            new ReportUseCase(new FakeReportJobQueue(), new FakeReportRepository(), new SystemClock()),
            TempArtifactDirectory.CreatePath("reports-schedule"),
            scheduleSettings: new ReportScheduleSettingsUseCase(store));

        await viewModel.InitializeAsync();
        Assert.True(viewModel.HasScheduleSettings);
        Assert.Equal(TimeSpan.FromMinutes(30), viewModel.ScheduleTime);
        Assert.True(viewModel.ScheduleEnabled);

        viewModel.ScheduleTime = new TimeSpan(2, 15, 0);
        viewModel.ScheduleEnabled = false;
        await viewModel.SaveScheduleCommand.ExecuteAsync(null);

        Assert.Equal(new TimeOnly(2, 15), store.Settings.DailyLocalTime);
        Assert.False(store.Settings.Enabled);
        Assert.Contains("已关闭", viewModel.ScheduleStatusMessage, StringComparison.Ordinal);
    });

    [Fact]
    public Task ReportsPanel_ExposesScheduleTimePickerAndToggle() => RunInHeadlessSession(async () =>
    {
        var store = new RecordingScheduleSettingsStore(
            new ReportScheduleSettings(new TimeOnly(0, 30), Enabled: true));
        using var voice = new VoiceControlViewModel(new FakeVoiceSession());
        using var shell = CreateShellWithVoice(
            voice,
            new ReportScheduleSettingsUseCase(store));
        var window = new MainWindow { DataContext = shell };
        window.Show();
        await shell.InitializeAsync();
        shell.SelectedSectionIndex = 6;

        await WaitUntilAsync(() => window.GetVisualDescendants().OfType<TimePicker>().Any());
        var picker = window.GetVisualDescendants().OfType<TimePicker>().Single();
        Assert.Equal(TimeSpan.FromMinutes(30), picker.SelectedTime);

        var toggle = window.GetVisualDescendants().OfType<ToggleSwitch>()
            .SingleOrDefault(item => AutomationProperties.GetName(item) == "启用自动日报");
        Assert.NotNull(toggle);
        Assert.True(toggle!.IsChecked);
        window.Close();
    });

    [Fact]
    public Task FullWindow_DataPageExposesBackupAndRetentionControls() => RunInHeadlessSession(async () =>
    {
        var data = new DataManagementViewModel(
            new FakeDataDeletionService(),
            maintenance: new RecordingMaintenanceService(),
            retention: new RecordingRetentionService(),
            retentionSettings: new RetentionSettingsUseCase(
                new RecordingRetentionSettingsStore(RetentionSettings.Default)));
        using var voice = new VoiceControlViewModel(new FakeVoiceSession());
        using var shell = CreateShellWithVoice(voice, data: data);
        var window = new MainWindow { DataContext = shell };
        window.Show();
        await shell.InitializeAsync();
        shell.SelectedSectionIndex = 4;

        await WaitUntilAsync(() => window.GetVisualDescendants().OfType<Button>()
            .Any(button => AutomationProperties.GetName(button) == "导出加密备份"));

        Assert.NotNull(window.GetVisualDescendants().OfType<Button>()
            .SingleOrDefault(button => AutomationProperties.GetName(button) == "从备份恢复"));
        Assert.NotNull(window.GetVisualDescendants().OfType<Button>()
            .SingleOrDefault(button => AutomationProperties.GetName(button) == "保存保留设置"));

        var numbers = window.GetVisualDescendants().OfType<NumericUpDown>().ToArray();
        Assert.Equal(3, numbers.Length);
        Assert.Equal(30m, numbers[0].Value);
        Assert.Equal(7m, numbers[1].Value);
        Assert.Equal(0m, numbers[2].Value);

        // The numeric fields must round-trip into the view model, not just render.
        numbers[0].Value = 14;
        await WaitUntilAsync(() => data.MediaRetentionDays == 14);
        window.Close();
    });

    [Fact]
    public Task FullWindow_RetentionSpinnersUseCompactVerticalArrows() => RunInHeadlessSession(async () =>
    {
        var data = new DataManagementViewModel(
            new FakeDataDeletionService(),
            retention: new RecordingRetentionService(),
            retentionSettings: new RetentionSettingsUseCase(
                new RecordingRetentionSettingsStore(RetentionSettings.Default)));
        using var voice = new VoiceControlViewModel(new FakeVoiceSession());
        using var shell = CreateShellWithVoice(voice, data: data);
        var window = new MainWindow { DataContext = shell };
        window.Show();
        await shell.InitializeAsync();
        shell.SelectedSectionIndex = 4;

        await WaitUntilAsync(() => window.GetVisualDescendants().OfType<NumericUpDown>().Count() == 3);
        foreach (var number in window.GetVisualDescendants().OfType<NumericUpDown>())
        {
            var spinner = number.GetVisualDescendants().OfType<ButtonSpinner>().Single();
            var panel = spinner.GetVisualDescendants().OfType<StackPanel>()
                .Single(item => item.Name == "PART_SpinnerPanel");
            Assert.Equal(Orientation.Vertical, panel.Orientation);

            var buttons = panel.GetVisualDescendants().OfType<RepeatButton>().ToArray();
            Assert.Equal(2, buttons.Length);
            Assert.All(buttons, button =>
            {
                Assert.Equal(18, button.Width);
                Assert.Equal(14, button.Height);
            });
        }

        window.Close();
    });

    [Fact]
    public Task FullWindow_DataProgressStaysInsideItsOwnCard() => RunInHeadlessSession(async () =>
    {
        var maintenance = new RecordingMaintenanceService();
        var gate = new TaskCompletionSource<bool>();
        maintenance.BackupGate = gate;
        var backupPath = Path.Combine(
            TempArtifactDirectory.CreatePath("backup-progress"),
            "aiassistant-backup-progress.zip");
        var data = new DataManagementViewModel(
            new FakeDataDeletionService(),
            maintenance: maintenance,
            retention: new RecordingRetentionService(),
            retentionSettings: new RetentionSettingsUseCase(
                new RecordingRetentionSettingsStore(RetentionSettings.Default)))
        {
            SaveFilePicker = new FakeSaveFilePicker(backupPath)
        };
        using var voice = new VoiceControlViewModel(new FakeVoiceSession());
        using var shell = CreateShellWithVoice(voice, data: data);
        var window = new MainWindow { DataContext = shell };
        window.Show();
        await shell.InitializeAsync();
        shell.SelectedSectionIndex = 4;

        await WaitUntilAsync(() => window.GetVisualDescendants().OfType<Button>()
            .Any(button => AutomationProperties.GetName(button) == "导出加密备份"));
        await data.BackupCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => data.IsAwaitingBackupKey);
        data.BackupKeyConfirmed = true;
        var backup = data.ConfirmBackupCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => data.IsBackingUp);

        // The delete card used to host the only progress bar, so backup work showed up there.
        var bars = window.GetVisualDescendants().OfType<ProgressBar>().ToArray();
        var backupBar = bars.Single(bar => AutomationProperties.GetName(bar) == "备份恢复进度");
        var deleteBar = bars.Single(bar => AutomationProperties.GetName(bar) == "删除进度");
        var retentionBar = bars.Single(bar => AutomationProperties.GetName(bar) == "保留清理进度");
        Assert.True(backupBar.IsVisible);
        Assert.False(deleteBar.IsVisible);
        Assert.False(retentionBar.IsVisible);
        Assert.Equal("数据备份与恢复", OwningCardTitle(backupBar));
        Assert.Equal("删除本地数据", OwningCardTitle(deleteBar));
        Assert.Equal("保留与清理", OwningCardTitle(retentionBar));

        gate.SetResult(true);
        await backup;

        Assert.Contains("已导出加密备份", data.BackupStatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("已导出加密备份", data.DeleteStatusMessage, StringComparison.Ordinal);
        window.Close();
    });

    [Fact]
    public Task FullWindow_BusySectionDisablesEveryCardThatStartsWork() => RunInHeadlessSession(async () =>
    {
        var maintenance = new RecordingMaintenanceService { BackupGate = new TaskCompletionSource<bool>() };
        var data = new DataManagementViewModel(
            new FakeDataDeletionService(),
            maintenance: maintenance,
            retention: new RecordingRetentionService(),
            retentionSettings: new RetentionSettingsUseCase(
                new RecordingRetentionSettingsStore(RetentionSettings.Default)))
        {
            SaveFilePicker = new FakeSaveFilePicker(Path.Combine(
                TempArtifactDirectory.CreatePath("busy-buttons"),
                "aiassistant-busy.zip"))
        };
        using var voice = new VoiceControlViewModel(new FakeVoiceSession());
        using var shell = CreateShellWithVoice(voice, data: data);
        var window = new MainWindow { DataContext = shell };
        window.Show();
        await shell.InitializeAsync();
        shell.SelectedSectionIndex = 4;

        await WaitUntilAsync(() => HasButton(window, "导出加密备份…"));
        await data.BackupCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => data.IsAwaitingBackupKey);
        data.BackupKeyConfirmed = true;
        var backup = data.ConfirmBackupCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => data.IsBackingUp);

        // Only one section may run at a time. The delete card used to stay clickable during a
        // backup and could start a purge next to it; every work-starting button must be disabled.
        Assert.False(ButtonByText(window, "删除该日数据").IsEnabled);
        Assert.False(ButtonByText(window, "清空全部本地数据").IsEnabled);
        Assert.False(ButtonByText(window, "保存保留设置").IsEnabled);
        Assert.False(ButtonByText(window, "立即清理").IsEnabled);

        // The command refuses as well, so automation cannot bypass the disabled buttons.
        await data.DeleteDateCommand.ExecuteAsync(null);
        Assert.False(data.HasPending);

        maintenance.BackupGate!.SetResult(true);
        await backup;
        await WaitUntilAsync(() => !data.IsBusy);
        Assert.True(ButtonByText(window, "删除该日数据").IsEnabled);
        window.Close();
    });

    [Fact]
    public Task FullWindow_EveryProgressBarUsesTheSameDeterminatePulse() => RunInHeadlessSession(async () =>
    {
        var data = new DataManagementViewModel(
            new FakeDataDeletionService(),
            maintenance: new RecordingMaintenanceService(),
            retention: new RecordingRetentionService(),
            retentionSettings: new RetentionSettingsUseCase(
                new RecordingRetentionSettingsStore(RetentionSettings.Default)));
        using var voice = new VoiceControlViewModel(new FakeVoiceSession());
        using var shell = CreateShellWithVoice(voice, data: data);
        var window = new MainWindow { DataContext = shell };
        window.Show();
        await shell.InitializeAsync();

        // Indeterminate bars freeze once their tab content is detached and re-attached, and they
        // look different from the determinate pulse used by 日报/检索; every panel must match.
        Type[] tabViews =
        [
            typeof(Views.Tabs.TimelineTabView),
            typeof(Views.Tabs.SearchTabView),
            typeof(Views.Tabs.PrivacyCaptureTabView),
            typeof(Views.Tabs.VoiceTabView),
            typeof(Views.Tabs.DataExportTabView),
            typeof(Views.Tabs.DiagnosticsTabView),
            typeof(Views.Tabs.ReportsTabView)
        ];

        var names = new List<string>();
        for (var index = 0; index < tabViews.Length; index++)
        {
            shell.SelectedSectionIndex = index;
            var expected = tabViews[index];
            await WaitUntilAsync(() => window.GetVisualDescendants().Any(visual => visual.GetType() == expected));
            foreach (var bar in window.GetVisualDescendants().OfType<ProgressBar>())
            {
                Assert.False(bar.IsIndeterminate);
                Assert.Equal(0, bar.Minimum);
                Assert.Equal(100, bar.Maximum);
                names.Add(AutomationProperties.GetName(bar) ?? string.Empty);
            }
        }

        // The data page keeps one bar per card; all three are on the shared pulse now.
        Assert.Contains("删除进度", names);
        Assert.Contains("备份恢复进度", names);
        Assert.Contains("保留清理进度", names);
        window.Close();
    });

    [Fact]
    public Task FullWindow_BackupKeyPanelRequiresSavedKeyConfirmation() => RunInHeadlessSession(async () =>
    {
        var data = new DataManagementViewModel(
            new FakeDataDeletionService(),
            maintenance: new RecordingMaintenanceService())
        {
            SaveFilePicker = new FakeSaveFilePicker(Path.Combine(
                TempArtifactDirectory.CreatePath("backup-key-panel"),
                "aiassistant-backup.zip"))
        };
        using var voice = new VoiceControlViewModel(new FakeVoiceSession());
        using var shell = CreateShellWithVoice(voice, data: data);
        var window = new MainWindow { DataContext = shell };
        window.Show();
        await shell.InitializeAsync();
        shell.SelectedSectionIndex = 4;

        await WaitUntilAsync(() => HasButton(window, "导出加密备份…"));
        Click(window, "导出加密备份…");
        await WaitUntilAsync(() => data.IsAwaitingBackupKey);
        await WaitUntilAsync(() => window.GetVisualDescendants().OfType<Button>()
            .Any(button => string.Equals(button.Content?.ToString(), "开始导出", StringComparison.Ordinal)));

        var start = window.GetVisualDescendants().OfType<Button>()
            .Single(button => string.Equals(button.Content?.ToString(), "开始导出", StringComparison.Ordinal));
        var keyBox = window.GetVisualDescendants().OfType<TextBox>()
            .Single(box => AutomationProperties.GetName(box) == "备份密钥");
        Assert.Equal(data.PendingBackupKey, keyBox.Text);
        Assert.False(start.IsEnabled);

        var confirm = window.GetVisualDescendants().OfType<CheckBox>()
            .Single(box => AutomationProperties.GetName(box) == "确认已保存备份密钥");
        confirm.IsChecked = true;
        await WaitUntilAsync(() => data.BackupKeyConfirmed);
        Assert.True(start.IsEnabled);
        window.Close();
    });

    [Fact]
    public Task FullWindow_RestoreKeyPanelEnablesValidationAfterInput() => RunInHeadlessSession(async () =>
    {
        var data = new DataManagementViewModel(
            new FakeDataDeletionService(),
            maintenance: new RecordingMaintenanceService())
        {
            OpenFilePicker = new FakeOpenFilePicker(Path.Combine(
                TempArtifactDirectory.CreatePath("restore-key-panel"),
                "aiassistant-backup.zip"))
        };
        using var voice = new VoiceControlViewModel(new FakeVoiceSession());
        using var shell = CreateShellWithVoice(voice, data: data);
        var window = new MainWindow { DataContext = shell };
        window.Show();
        await shell.InitializeAsync();
        shell.SelectedSectionIndex = 4;

        await WaitUntilAsync(() => HasButton(window, "从备份恢复…"));
        Click(window, "从备份恢复…");
        await WaitUntilAsync(() => data.IsAwaitingRestoreKey);
        await WaitUntilAsync(() => window.GetVisualDescendants().OfType<Button>()
            .Any(button => AutomationProperties.GetName(button) == "解密并校验备份"));

        var validate = window.GetVisualDescendants().OfType<Button>()
            .Single(button => AutomationProperties.GetName(button) == "解密并校验备份");
        Assert.False(validate.IsEnabled);

        var input = window.GetVisualDescendants().OfType<TextBox>()
            .Single(box => AutomationProperties.GetName(box) == "备份密钥输入");
        input.Text = TestBackupKey;
        await WaitUntilAsync(() => data.RestoreKeyInput == TestBackupKey);
        Assert.True(validate.IsEnabled);
        window.Close();
    });

    [Fact]
    public async Task DataManagement_ExportsBackupAndStagesRestoreBehindConfirmation()
    {
        var maintenance = new RecordingMaintenanceService();
        var backupPath = Path.Combine(
            TempArtifactDirectory.CreatePath("backup-export"),
            "aiassistant-backup-test.zip");
        var restorePath = Path.Combine(
            TempArtifactDirectory.CreatePath("backup-restore"),
            "aiassistant-restore-test.zip");
        var viewModel = new DataManagementViewModel(
            new FakeDataDeletionService(),
            maintenance: maintenance)
        {
            SaveFilePicker = new FakeSaveFilePicker(backupPath),
            OpenFilePicker = new FakeOpenFilePicker(restorePath)
        };

        // Export first generates a key and waits for the user to save it; nothing is written yet.
        await viewModel.BackupCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsAwaitingBackupKey);
        Assert.Null(maintenance.BackupPath);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.PendingBackupKey));

        viewModel.BackupKeyConfirmed = true;
        await viewModel.ConfirmBackupCommand.ExecuteAsync(null);
        Assert.Equal(backupPath, maintenance.BackupPath);
        Assert.Equal(TestBackupKey, maintenance.BackupKey);
        Assert.True(viewModel.BackupExportCompleted);
        Assert.Contains("已导出加密备份", viewModel.BackupStatusMessage, StringComparison.Ordinal);

        // The same command then closes the key panel.
        await viewModel.ConfirmBackupCommand.ExecuteAsync(null);
        Assert.False(viewModel.IsAwaitingBackupKey);

        // First click picks the archive and asks for its key; nothing is staged yet.
        await viewModel.RestoreCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsAwaitingRestoreKey);
        Assert.False(viewModel.HasPendingRestore);
        Assert.Null(maintenance.StagedPath);

        viewModel.RestoreKeyInput = TestBackupKey;
        await viewModel.ValidateRestoreKeyCommand.ExecuteAsync(null);
        Assert.Equal(restorePath, maintenance.StagedPath);
        Assert.Equal(TestBackupKey, maintenance.StagedKey);
        Assert.True(viewModel.HasPendingRestore);
        Assert.False(viewModel.RestartAvailable);
        Assert.Contains("aiassistant-restore-test.zip", viewModel.PendingText, StringComparison.Ordinal);

        // The danger confirmation commits the already validated staging.
        await viewModel.RestoreCommand.ExecuteAsync(null);
        Assert.True(maintenance.Committed);
        Assert.False(viewModel.HasPending);
        Assert.True(viewModel.RestartAvailable);
        Assert.Contains("重启后", viewModel.BackupStatusMessage, StringComparison.Ordinal);

        var restarted = false;
        viewModel.RestartRequested += (_, _) => restarted = true;
        viewModel.RestartCommand.Execute(null);
        Assert.True(restarted);

        // Changing your mind must stop the staged restore from replacing data on the next start.
        await viewModel.CancelStagedRestoreCommand.ExecuteAsync(null);
        Assert.True(maintenance.Discarded);
        Assert.False(viewModel.RestartAvailable);
        Assert.Contains("已取消", viewModel.BackupStatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DataManagement_WrongBackupKeyExplainsFailure()
    {
        var maintenance = new RecordingMaintenanceService
        {
            StageRestoreFailure = new StorageAuthenticationException("Backup key is incorrect.")
        };
        var viewModel = new DataManagementViewModel(new FakeDataDeletionService(), maintenance: maintenance)
        {
            OpenFilePicker = new FakeOpenFilePicker(Path.Combine(
                TempArtifactDirectory.CreatePath("wrong-backup-key"),
                "aiassistant-backup.zip"))
        };

        await viewModel.RestoreCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsAwaitingRestoreKey);

        viewModel.RestoreKeyInput = "WRONG-KEY";
        await viewModel.ValidateRestoreKeyCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsAwaitingRestoreKey);
        Assert.False(viewModel.HasPendingRestore);
        Assert.Contains("密钥不正确", viewModel.BackupErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DataManagement_SavesRetentionPolicyAndRunsSweep()
    {
        var retention = new RecordingRetentionService();
        var store = new RecordingRetentionSettingsStore(RetentionSettings.Default);
        var viewModel = new DataManagementViewModel(
            new FakeDataDeletionService(),
            retention: retention,
            retentionSettings: new RetentionSettingsUseCase(store));

        Assert.Equal(30, viewModel.MediaRetentionDays);
        Assert.Contains("媒体保留 30 天", viewModel.RetentionPolicyText, StringComparison.Ordinal);
        Assert.Contains("命令音频保留 7 天", viewModel.RetentionPolicyText, StringComparison.Ordinal);

        viewModel.MediaRetentionDays = 14;
        viewModel.CommandAudioRetentionDays = 3;
        viewModel.MaxStorageGigabytes = 20;
        await viewModel.SaveRetentionCommand.ExecuteAsync(null);

        Assert.Equal(14, store.Settings.MediaRetentionDays);
        Assert.Equal(3, store.Settings.CommandAudioRetentionDays);
        Assert.Equal(20, store.Settings.MaxStorageGigabytes);
        // The delete card must not repeat concrete windows; only the retention card owns them.
        Assert.Contains("保留与清理", viewModel.RetentionNotice, StringComparison.Ordinal);
        Assert.DoesNotContain("14 天", viewModel.RetentionNotice, StringComparison.Ordinal);
        Assert.DoesNotContain("20 GB", viewModel.RetentionNotice, StringComparison.Ordinal);
        Assert.Contains("20 GB", viewModel.RetentionPolicyText, StringComparison.Ordinal);
        // Saving a lowered cap sweeps immediately so the write gate does not start rejecting blobs.
        Assert.Equal(1, retention.SweepCount);

        retention.NextPurged = 4;
        await viewModel.RunRetentionSweepCommand.ExecuteAsync(null);
        Assert.Equal(2, retention.SweepCount);
        Assert.Contains("4 个", viewModel.RetentionStatusMessage, StringComparison.Ordinal);
    }

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
        Assert.Contains("已删除", viewModel.DeleteStatusMessage, StringComparison.Ordinal);
    });

    [Fact]
    public Task FullWindow_DataDeletionRequiresTwoStepConfirmation() => RunInHeadlessSession(async () =>
    {
        using var shell = CreateShell(out var data);
        var window = new MainWindow { DataContext = shell };
        window.Show();
        await shell.InitializeAsync();

        shell.SelectedSectionIndex = 4; // 数据与导出
        await WaitUntilAsync(() => HasButton(window, "删除该日数据"));
        Click(window, "删除该日数据");
        await WaitUntilAsync(() => data.HasPending);
        Assert.True(HasButton(window, "确认删除"));

        Click(window, "确认删除");
        await WaitUntilAsync(() => !data.HasPending);
        Assert.Contains("已删除", data.DeleteStatusMessage, StringComparison.Ordinal);
        window.Close();
    });

    /// <summary>Heading of the nearest card that contains the control.</summary>
    private static string OwningCardTitle(Control control) =>
        control.GetVisualAncestors()
            .OfType<Border>()
            .Select(border => border.GetVisualDescendants().OfType<TextBlock>()
                .Select(text => text.Text)
                .FirstOrDefault(text => text is "删除本地数据" or "数据备份与恢复" or "保留与清理"))
            .FirstOrDefault(title => title is not null) ?? string.Empty;

    private static bool HasButton(Window window, string text) =>
        window.GetVisualDescendants().OfType<Button>()
            .Any(button => string.Equals(button.Content?.ToString(), text, StringComparison.Ordinal));

    private static Button ButtonByText(Window window, string text) =>
        window.GetVisualDescendants().OfType<Button>()
            .First(button => string.Equals(button.Content?.ToString(), text, StringComparison.Ordinal));

    private static bool HasTextBox(Window window, string name) =>
        window.GetVisualDescendants().OfType<TextBox>()
            .Any(control => string.Equals(AutomationProperties.GetName(control), name, StringComparison.Ordinal));

    private static TextBox TextBoxByName(Window window, string name) =>
        window.GetVisualDescendants().OfType<TextBox>()
            .First(control => string.Equals(AutomationProperties.GetName(control), name, StringComparison.Ordinal));

    /// <summary>Real pointer click at the control's position, falling back to the bound command.</summary>
    private static void Click(Window window, string buttonText)
    {
        var button = ButtonByText(window, buttonText);
        var point = button.Bounds.Width > 0
            ? button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)
            : null;
        if (point is not null)
        {
            window.MouseDown(point.Value, MouseButton.Left);
            window.MouseUp(point.Value, MouseButton.Left);
        }
        else
        {
            button.Command?.Execute(button.CommandParameter);
        }
    }

    [Fact]
    public async Task ModelConfigEditor_PassesWriteOnlyApiKeyAndKeepsHintsInSync()
    {
        var admin = new RecordingModelConfigurationAdmin();
        var editor = new ModelConfigurationEditorViewModel(admin)
        {
            ChatApiKey = "sk-chat-123",
            EmbeddingApiKey = "sk-embed-456"
        };

        await editor.SaveModelConfigurationCommand.ExecuteAsync(null);

        Assert.NotNull(admin.Saved);
        Assert.Equal("sk-chat-123", admin.Saved!.Chat.ApiKey);
        Assert.Equal("sk-embed-456", admin.Saved.Embedding.ApiKey);
        Assert.Equal(string.Empty, editor.ChatApiKey);
        Assert.Equal(string.Empty, editor.EmbeddingApiKey);
        Assert.Contains("chat-endpoint-key", editor.ChatApiKeyHint, StringComparison.Ordinal);
        Assert.Contains("embedding-endpoint-key", editor.EmbeddingApiKeyHint, StringComparison.Ordinal);
        Assert.Contains("正在重启应用", editor.ModelConfigStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelConfigEditor_SwitchingTierReplacesLoopbackPlaceholder()
    {
        var editor = new ModelConfigurationEditorViewModel(new RecordingModelConfigurationAdmin());
        Assert.Equal("http://127.0.0.1:11434", editor.ChatBaseUrl);

        // Local → public must not keep a loopback address on a remote tier.
        editor.ChatTier = editor.TierOptions.First(option => option.Value == "PublicCloud");
        Assert.Equal("https://", editor.ChatBaseUrl);

        // A real user URL survives tier switches.
        editor.ChatBaseUrl = "https://api.deepseek.com";
        editor.ChatTier = editor.TierOptions.First(option => option.Value == "CorporateIntranet");
        Assert.Equal("https://api.deepseek.com", editor.ChatBaseUrl);

        // Remote → local restores the Ollama default instead of keeping an https address.
        editor.ChatTier = editor.TierOptions.First(option => option.Value == "LocalOffline");
        Assert.Equal("http://127.0.0.1:11434", editor.ChatBaseUrl);
    }

    private sealed class RecordingModelConfigurationAdmin : IModelConfigurationAdmin
    {
        private string? _chatSecret;
        private string? _embeddingSecret;

        public ModelConfigurationView? Saved { get; private set; }

        public ModelConfigurationView Current => new(
            new ModelEndpointView("http://127.0.0.1:11434", "gemma4:12b", "LocalOffline", ApiKeySecret: _chatSecret),
            new ModelEndpointView("http://127.0.0.1:11434", "embeddinggemma-2:740m", "LocalOffline", ApiKeySecret: _embeddingSecret));

        public Task SaveAsync(ModelConfigurationView update, CancellationToken ct)
        {
            Saved = update;
            if (update.Chat.ApiKey is { Length: > 0 })
            {
                _chatSecret = "chat-endpoint-key";
            }

            if (update.Embedding.ApiKey is { Length: > 0 })
            {
                _embeddingSecret = "embedding-endpoint-key";
            }

            return Task.CompletedTask;
        }
    }

    private sealed class RecordingScheduleSettingsStore(ReportScheduleSettings initial) : IReportScheduleSettingsStore
    {
        public ReportScheduleSettings Settings { get; private set; } = initial;

        public ReportScheduleSettings Load() => Settings;

        public Task SaveAsync(ReportScheduleSettings settings, CancellationToken ct)
        {
            Settings = settings;
            return Task.CompletedTask;
        }
    }

    private static ShellViewModel CreateShellWithVoice(
        VoiceControlViewModel voice,
        ReportScheduleSettingsUseCase? scheduleSettings = null,
        DataManagementViewModel? data = null)
    {
        var repository = new InMemoryEventRepository();
        var clock = new SystemClock();
        var coordinator = new FakeCaptureCoordinator();
        var diagnostics = new DiagnosticsUseCase(
            new FakeCapabilityProvider(),
            new FakeMaintenanceService(),
            coordinator,
            new FakeReportJobQueue());
        return new ShellViewModel(
            new TimelineViewModel(new TimelineUseCase(repository, new FakeMediaStore(), clock)),
            new CaptureControlViewModel(new CaptureControlUseCase(coordinator, new FakeAutoStartService())),
            new DiagnosticsViewModel(diagnostics),
            new ReportsViewModel(
                new ReportUseCase(new FakeReportJobQueue(), new FakeReportRepository(), clock),
                TempArtifactDirectory.CreatePath("reports-ui"),
                scheduleSettings: scheduleSettings),
            voice: voice,
            voiceNavigation: null,
            search: null,
            data: data ?? new DataManagementViewModel(new FakeDataDeletionService()));
    }


}
