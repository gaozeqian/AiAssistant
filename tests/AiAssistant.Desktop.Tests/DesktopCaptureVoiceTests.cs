using System.Runtime.CompilerServices;
using AiAssistant.Application.Policies;
using AiAssistant.Application.Ports;
using AiAssistant.Application.UseCases;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Desktop.ViewModels;
using AiAssistant.Desktop.Views;
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
