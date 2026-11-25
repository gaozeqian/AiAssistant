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
    public Task Diagnostics_SecretAdminSavesAndDeletesWithoutEchoingValue() => RunInHeadlessSession(async () =>
    {
        var admin = new FakeSecretAdmin();
        var diagnostics = new DiagnosticsUseCase(
            new FakeCapabilityProvider(),
            new FakeMaintenanceService(),
            new FakeCaptureCoordinator(),
            new FakeReportJobQueue());
        var viewModel = new DiagnosticsViewModel(diagnostics, mcp: null, exportDirectory: null, secrets: admin);

        viewModel.Secrets.SecretName = "corp-key";
        viewModel.Secrets.SecretValue = "secret-value";
        await viewModel.Secrets.SaveSecretCommand.ExecuteAsync(null);

        Assert.Contains("corp-key", viewModel.Secrets.SecretNames);
        Assert.Equal(string.Empty, viewModel.Secrets.SecretValue);
        Assert.DoesNotContain("secret-value", viewModel.Secrets.SecretStatus, StringComparison.Ordinal);

        viewModel.Secrets.SelectedSecret = "corp-key";
        await viewModel.Secrets.DeleteSecretCommand.ExecuteAsync(null);
        Assert.Empty(viewModel.Secrets.SecretNames);
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
        Assert.Contains("已删除", data.StatusMessage, StringComparison.Ordinal);
        window.Close();
    });

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
        Assert.Contains("重启应用后生效", editor.ModelConfigStatus, StringComparison.Ordinal);
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

    private static ShellViewModel CreateShellWithVoice(VoiceControlViewModel voice)
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
                TempArtifactDirectory.CreatePath("reports-ui")),
            voice: voice,
            voiceNavigation: null,
            search: null,
            data: new DataManagementViewModel(new FakeDataDeletionService()));
    }


}
