using System.Runtime.CompilerServices;
using AiAssistant.Application.Policies;
using AiAssistant.Application.Ports;
using AiAssistant.Application.UseCases;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Desktop.ViewModels;
using AiAssistant.Desktop.Views;
using AiAssistant.Desktop.Views.Tabs;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace AiAssistant.Desktop.Tests;

[Trait("Category", "UI")]
public sealed partial class DesktopViewModelTests
{
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
    public Task VoicePanel_TogglesSessionAndReflectsListeningState() => RunInHeadlessSession(async () =>
    {
        var session = new FakeVoiceSession();
        using var viewModel = new VoiceControlViewModel(session);
        Assert.False(viewModel.IsEnabled);
        Assert.Equal("语音休眠", viewModel.StatusText);

        await viewModel.ToggleCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsEnabled);
        Assert.Equal(VoiceSessionState.Sleeping, session.State);
        Assert.Equal("已启用 · 待唤醒", viewModel.StatusText);
        Assert.Contains("fake KWS", viewModel.CapabilityText, StringComparison.Ordinal);

        // The real controller only enters Listening after the wake word is detected.
        session.SimulateWake();
        Assert.Equal("正在聆听", viewModel.StatusText);

        await viewModel.ToggleCommand.ExecuteAsync(null);
        Assert.False(viewModel.IsEnabled);
        Assert.Equal("语音休眠", viewModel.StatusText);
    });

    [Fact]
    public Task VoicePanel_SessionFaultTurnsTheSwitchOffAndExplainsWhy() => RunInHeadlessSession(async () =>
    {
        var session = new FakeVoiceSession();
        using var viewModel = new VoiceControlViewModel(session);
        await viewModel.ToggleCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsEnabled);

        session.SimulateFault("音频采集已停止");

        Assert.False(viewModel.IsEnabled);
        Assert.Equal("语音已停止", viewModel.StatusText);
        Assert.Contains("音频采集已停止", viewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("重新启用", viewModel.ErrorMessage, StringComparison.Ordinal);
    });

    [Fact]
    public Task VoicePanel_ListsRecordedCommandsAndReplaysStoredAudio() => RunInHeadlessSession(async () =>
    {
        var log = new FakeVoiceCommandLog();
        log.Records.Add(new VoiceCommandLogRecord(
            "cmd_1",
            DateTimeOffset.UtcNow.AddMinutes(-2),
            "你好时光",
            "暂停采集",
            "PauseCapture",
            "{}",
            ModelEndpointTier.LocalOffline,
            null,
            "cnt_audio_1"));
        log.Records.Add(new VoiceCommandLogRecord(
            "cmd_2",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            "你好时光",
            "问一下昨天的进展",
            "AskQuestion",
            "{}",
            ModelEndpointTier.LocalOffline,
            null,
            null));
        // Retention already erased this clip; the row must say “已清理”, not “未留存”.
        log.Records.Add(new VoiceCommandLogRecord(
            "cmd_3",
            DateTimeOffset.UtcNow.AddSeconds(-30),
            "你好时光",
            "恢复采集",
            "ResumeCapture",
            "{}",
            ModelEndpointTier.LocalOffline,
            null,
            null,
            DateTimeOffset.UtcNow.AddMinutes(-5)));
        var player = new FakeCommandAudioPlayer();
        using var viewModel = new VoiceControlViewModel(new FakeVoiceSession(), log, player);

        await viewModel.InitializeAsync();

        Assert.True(viewModel.HasCommandHistory);
        Assert.Equal(3, viewModel.CommandHistory.Count);
        Assert.Equal("恢复采集", viewModel.CommandHistory[0].Transcript);
        Assert.Equal("已清理", viewModel.CommandHistory[0].AudioStateText);
        Assert.True(viewModel.CommandHistory[0].WasAudioCleared);
        Assert.Equal("问一下昨天的进展", viewModel.CommandHistory[1].Transcript);
        Assert.Equal("提问", viewModel.CommandHistory[1].IntentText);
        Assert.False(viewModel.CommandHistory[1].HasAudio);
        Assert.Equal("未留存", viewModel.CommandHistory[1].AudioStateText);
        Assert.False(viewModel.CommandHistory[1].WasAudioCleared);
        Assert.True(viewModel.CommandHistory[2].HasAudio);
        Assert.Equal("已留存", viewModel.CommandHistory[2].AudioStateText);

        await viewModel.ReplayCommand.ExecuteAsync(viewModel.CommandHistory[2]);
        Assert.Equal("cnt_audio_1", player.PlayedContentId);
        Assert.Contains("已重听", viewModel.CommandHistoryStatus, StringComparison.Ordinal);

        await viewModel.ReplayCommand.ExecuteAsync(viewModel.CommandHistory[1]);
        Assert.Contains("没有留存", viewModel.ErrorMessage, StringComparison.Ordinal);

        await viewModel.ReplayCommand.ExecuteAsync(viewModel.CommandHistory[0]);
        Assert.Contains("已按保留期清理", viewModel.ErrorMessage, StringComparison.Ordinal);
    });

    [Fact]
    public Task VoicePanel_ReplayStateIsPerRow_SoOneSlowClipDoesNotDisableEveryButton() => RunInHeadlessSession(async () =>
    {
        var log = new FakeVoiceCommandLog();
        log.Records.Add(new VoiceCommandLogRecord(
            "cmd_a",
            DateTimeOffset.UtcNow.AddMinutes(-2),
            "你好时光",
            "暂停采集",
            "PauseCapture",
            "{}",
            ModelEndpointTier.LocalOffline,
            null,
            "cnt_a"));
        log.Records.Add(new VoiceCommandLogRecord(
            "cmd_b",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            "你好时光",
            "恢复采集",
            "ResumeCapture",
            "{}",
            ModelEndpointTier.LocalOffline,
            null,
            "cnt_b"));
        var player = new ControllableCommandAudioPlayer();
        using var viewModel = new VoiceControlViewModel(new FakeVoiceSession(), log, player);
        await viewModel.InitializeAsync();

        var first = viewModel.CommandHistory.Single(item => item.Record.Id == "cmd_a");
        var second = viewModel.CommandHistory.Single(item => item.Record.Id == "cmd_b");

        // A clip that never finishes must only mark its own row as playing.
        var firstRun = viewModel.ReplayCommand.ExecuteAsync(first);
        Assert.True(first.IsPlaying);
        Assert.False(first.CanReplay);
        Assert.Equal("播放中", first.ReplayLabel);
        Assert.True(viewModel.ReplayCommand.CanExecute(second));
        Assert.True(second.CanReplay);

        var secondRun = viewModel.ReplayCommand.ExecuteAsync(second);
        await firstRun;
        Assert.False(first.IsPlaying);
        Assert.True(second.IsPlaying);

        player.Complete("cnt_b");
        await secondRun;
        Assert.False(second.IsPlaying);
        Assert.Contains("已重听", viewModel.CommandHistoryStatus, StringComparison.Ordinal);

        // Both rows are usable again once playback returns.
        Assert.True(first.CanReplay);
        Assert.True(second.CanReplay);
        Assert.True(viewModel.ReplayCommand.CanExecute(first));
    });

    [Fact]
    public Task VoicePanel_CommandAudioOptIn_IsOffUntilExplicitlyEnabled() => RunInHeadlessSession(async () =>
    {
        var session = new FakeVoiceSession();
        using var viewModel = new VoiceControlViewModel(session);
        Assert.False(viewModel.RecordsCommandAudio);
        Assert.Contains("不落盘", viewModel.CommandAudioStatus, StringComparison.Ordinal);

        await viewModel.ToggleCommandAudioCommand.ExecuteAsync(null);
        Assert.True(viewModel.RecordsCommandAudio);
        Assert.True(session.RecordsCommandAudio);
        Assert.Contains("加密", viewModel.CommandAudioStatus, StringComparison.Ordinal);

        await viewModel.ToggleCommandAudioCommand.ExecuteAsync(null);
        Assert.False(viewModel.RecordsCommandAudio);
        Assert.False(session.RecordsCommandAudio);
        Assert.Contains("不落盘", viewModel.CommandAudioStatus, StringComparison.Ordinal);
    });


    [Fact]
    public Task FullWindow_UserPausesAndResumesCapture_UpdatesStatusBanner() => RunInHeadlessSession(async () =>
    {
        using var shell = CreateShell();
        var window = new MainWindow { DataContext = shell };
        window.Show();
        await shell.InitializeAsync();

        Assert.Equal("正在本机采集", shell.CaptureStatus);

        shell.SelectedSectionIndex = 2; // 隐私与控制
        await WaitUntilAsync(() => HasButton(window, "暂停采集"));
        Click(window, "暂停采集");
        await WaitUntilAsync(() => shell.Capture.IsPaused);
        Assert.Equal("已暂停 · 不补采暂停期间内容", shell.CaptureStatus);
        Assert.True(HasButton(window, "恢复采集"));

        Click(window, "恢复采集");
        await WaitUntilAsync(() => !shell.Capture.IsPaused);
        Assert.Equal("正在本机采集", shell.CaptureStatus);
        window.Close();
    });


    [Fact]
    public Task FullWindow_VoiceCommandHistoryExposesReplayButton() => RunInHeadlessSession(async () =>
    {
        var log = new FakeVoiceCommandLog();
        log.Records.Add(new VoiceCommandLogRecord(
            "cmd_ui",
            DateTimeOffset.UtcNow,
            "你好时光",
            "暂停采集",
            "PauseCapture",
            "{}",
            ModelEndpointTier.LocalOffline,
            null,
            "cnt_audio_ui"));
        var player = new FakeCommandAudioPlayer();
        using var voice = new VoiceControlViewModel(new FakeVoiceSession(), log, player);
        using var shell = CreateShellWithVoice(voice);
        var window = new MainWindow { DataContext = shell };
        window.Show();
        await shell.InitializeAsync();
        shell.SelectedSectionIndex = 3;

        await WaitUntilAsync(() => window.GetVisualDescendants().OfType<Button>()
            .Any(button => AutomationProperties.GetName(button) == "重听命令音频"));
        var replay = window.GetVisualDescendants().OfType<Button>()
            .Single(button => AutomationProperties.GetName(button) == "重听命令音频");
        Assert.True(replay.IsVisible);
        Assert.NotNull(replay.Command);
        Assert.True(replay.Command!.CanExecute(replay.CommandParameter));

        replay.Command.Execute(replay.CommandParameter);
        await WaitUntilAsync(() => player.PlayedContentId is not null);
        Assert.Equal("cnt_audio_ui", player.PlayedContentId);
        window.Close();
    });

    [Fact]
    public Task FullWindow_VoiceCommandHistoryScrollsInsideItsCard() => RunInHeadlessSession(async () =>
    {
        var log = new FakeVoiceCommandLog();
        for (var i = 0; i < 30; i++)
        {
            log.Records.Add(new VoiceCommandLogRecord(
                $"cmd_scroll_{i}",
                DateTimeOffset.UtcNow.AddMinutes(-i),
                "你好时光",
                $"命令 {i}",
                "PauseCapture",
                "{}",
                ModelEndpointTier.LocalOffline,
                null,
                null,
                // The newest row was already swept by retention and must be toned as “已清理”.
                i == 0 ? DateTimeOffset.UtcNow.AddMinutes(-5) : null));
        }

        using var voice = new VoiceControlViewModel(new FakeVoiceSession(), log);
        using var shell = CreateShellWithVoice(voice);
        var window = new MainWindow { DataContext = shell };
        window.Show();
        await shell.InitializeAsync();
        shell.SelectedSectionIndex = 3;

        await WaitUntilAsync(() => window.GetVisualDescendants().OfType<VoiceTabView>().Any());
        var tab = window.GetVisualDescendants().OfType<VoiceTabView>().Single();
        // The page itself must not scroll any more; only the history card does.
        Assert.IsType<Grid>(tab.Content);

        var list = window.GetVisualDescendants().OfType<ItemsControl>()
            .Single(control => ReferenceEquals(control.ItemsSource, voice.CommandHistory));
        var scroller = list.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        Assert.NotNull(scroller);
        Assert.Equal(ScrollBarVisibility.Auto, scroller!.VerticalScrollBarVisibility);

        tab.UpdateLayout();
        Assert.True(
            scroller.Extent.Height > scroller.Viewport.Height,
            $"expected the card to scroll, extent={scroller.Extent.Height} viewport={scroller.Viewport.Height}");

        var clearedState = window.GetVisualDescendants().OfType<TextBlock>()
            .First(block => block.Text == "已清理");
        Assert.Contains("cleared", clearedState.Classes);
        window.Close();
    });

    [Fact]
    public Task FullWindow_UserTogglesVoiceOnAndOff() => RunInHeadlessSession(async () =>
    {
        var voice = new VoiceControlViewModel(new FakeVoiceSession());
        using var shell = CreateShellWithVoice(voice);
        var window = new MainWindow { DataContext = shell };
        window.Show();
        await shell.InitializeAsync();

        shell.SelectedSectionIndex = 3; // 语音
        await WaitUntilAsync(() => window.GetVisualDescendants().OfType<ToggleSwitch>()
            .Any(ts => AutomationProperties.GetName(ts) == "启用语音"));

        var toggle = window.GetVisualDescendants().OfType<ToggleSwitch>()
            .Single(ts => AutomationProperties.GetName(ts) == "启用语音");
        Assert.NotNull(toggle.Command);

        toggle.Command!.Execute(toggle.CommandParameter);
        await WaitUntilAsync(() => voice.IsEnabled);
        Assert.Equal("已启用 · 待唤醒", voice.StatusText);
        Assert.True(toggle.IsChecked);

        toggle.Command.Execute(toggle.CommandParameter);
        await WaitUntilAsync(() => !voice.IsEnabled);
        Assert.Equal("语音休眠", voice.StatusText);
        window.Close();
    });


}
