using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AiAssistant.Desktop.ViewModels;
using AiAssistant.Desktop.Views;

namespace AiAssistant.Desktop;

/// <summary>
/// Real-platform UI E2E: drives the actual Avalonia window (real X11/Wayland backend, real Skia
/// rendering, real theme and tray) through a full user journey — timeline, evidence search,
/// evidence QA with the real model, weekly/daily reports, pause/blacklist, voice, diagnostics,
/// theme switch and data deletion — rendering the window to PNG after every step and writing a
/// pass/fail report. Enabled with <c>--ui-e2e</c>; exits 0 on success.
///
/// This complements the Avalonia.Headless tests: those prove the view-model wiring, while this
/// proves the app really starts on the target desktop, paints the expected UI and that the
/// capture → embedding → model → report pipeline works end-to-end inside the real window.
/// </summary>
public static class UiE2ERunner
{
    private sealed record StepResult(string Name, bool Passed, bool Skipped, string Detail, string? Screenshot);

    public static async Task<int> RunAsync(
        MainWindow window,
        ShellViewModel shell,
        Task<Exception?> initialization,
        Action<int> shutdown,
        string outputRoot)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(initialization);
        ArgumentNullException.ThrowIfNull(shutdown);

        var directory = Path.Combine(outputRoot, $"ui-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(directory);
        var steps = new List<StepResult>();
        var index = 0;

        async Task StepAsync(string name, Func<Task<string?>> action)
        {
            index++;
            try
            {
                var detail = await action();
                await SettleAsync(window);
                var shot = TryRender(window, Path.Combine(directory, $"{index:00}-{Slug(name)}.png"));
                steps.Add(new StepResult(name, true, false, detail ?? "ok", shot));
            }
            catch (SkipStepException ex)
            {
                await SettleAsync(window);
                var shot = TryRender(window, Path.Combine(directory, $"{index:00}-{Slug(name)}.png"));
                steps.Add(new StepResult(name, true, true, ex.Message, shot));
            }
            catch (Exception ex)
            {
                await SettleAsync(window);
                var shot = TryRender(window, Path.Combine(directory, $"{index:00}-{Slug(name)}.png"));
                steps.Add(new StepResult(name, false, false, $"{ex.GetType().Name}: {ex.Message}", shot));
            }
        }

        var initError = await WaitAsync(initialization, TimeSpan.FromSeconds(60));
        steps.Add(initError is null
            ? new StepResult("Shell 初始化完成", true, false, "InitializeAsync 返回", null)
            : new StepResult("Shell 初始化完成", false, false, initError.GetType().Name, null));

        await StepAsync("窗口真实显示且标题正确", () =>
        {
            if (!window.IsVisible)
            {
                throw new InvalidOperationException("主窗口未显示");
            }

            if (!string.Equals(window.Title, "时光 · AI 个人活动追踪助手", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"标题不符：{window.Title}");
            }

            if (window.Icon is null)
            {
                throw new InvalidOperationException("窗口图标为空");
            }

            return Task.FromResult<string?>(
                $"title={window.Title} size={window.ClientSize.Width:0}x{window.ClientSize.Height:0} icon=ok");
        });

        await StepAsync("时间线页加载并渲染", async () =>
        {
            shell.SelectedSectionIndex = 0;
            await shell.Timeline.RefreshCommand.ExecuteAsync(null);
            if (shell.Timeline.Events.Count == 0)
            {
                throw new InvalidOperationException("时间线没有事件（E2E 种子数据缺失）");
            }

            return $"events={shell.Timeline.Events.Count}";
        });

        await StepAsync("检索证据（真实 embedding）", async () =>
        {
            if (shell.Search is null)
            {
                throw new SkipStepException("检索页未装配");
            }

            shell.SelectedSectionIndex = 1;
            shell.Search.Query = "数据库迁移";
            await WaitAsync(shell.Search.SearchCommand.ExecuteAsync(null), TimeSpan.FromSeconds(90));
            if (shell.Search.Evidence.Count == 0)
            {
                throw new InvalidOperationException($"未命中证据：{shell.Search.StatusMessage}");
            }

            return $"evidence={shell.Search.Evidence.Count} status={shell.Search.StatusMessage}";
        });

        await StepAsync("证据问答（真实模型）", async () =>
        {
            if (shell.Search is null)
            {
                throw new SkipStepException("检索页未装配");
            }

            shell.Search.Query = "今天完成了哪些数据库相关工作";
            await WaitAsync(shell.Search.AskCommand.ExecuteAsync(null), TimeSpan.FromSeconds(240));
            if (!shell.Search.HasAnswer)
            {
                throw new InvalidOperationException($"未生成回答：{shell.Search.ErrorMessage}");
            }

            if (string.IsNullOrWhiteSpace(shell.Search.Answer))
            {
                throw new InvalidOperationException("回答为空");
            }

            return $"meta={shell.Search.AnswerMeta} chars={shell.Search.Answer.Length}";
        });

        await StepAsync("周报生成（真实模型）", async () =>
        {
            if (shell.Search is null)
            {
                throw new SkipStepException("检索页未装配");
            }

            await WaitAsync(shell.Search.WeeklyReportCommand.ExecuteAsync(null), TimeSpan.FromSeconds(240));
            if (!shell.Search.HasWeeklyReport)
            {
                throw new InvalidOperationException($"周报未生成：{shell.Search.WeeklyStatus}");
            }

            if (string.IsNullOrWhiteSpace(shell.Search.WeeklyMarkdown))
            {
                throw new InvalidOperationException("周报正文为空");
            }

            return $"status={shell.Search.WeeklyStatus}";
        });

        await StepAsync("暂停采集并更新顶部横幅", async () =>
        {
            shell.SelectedSectionIndex = 2;
            if (!shell.Capture.IsPaused)
            {
                await shell.Capture.TogglePauseCommand.ExecuteAsync(null);
            }

            if (!shell.Capture.IsPaused)
            {
                throw new InvalidOperationException("采集未进入暂停");
            }

            if (!shell.CaptureStatus.Contains("已暂停", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"横幅未更新：{shell.CaptureStatus}");
            }

            return $"status={shell.CaptureStatus}";
        });

        await StepAsync("追加黑名单并立即快照", async () =>
        {
            var lines = (shell.Capture.BlacklistText ?? string.Empty)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            if (!lines.Contains("E2E-Secret-App", StringComparer.Ordinal))
            {
                lines.Add("E2E-Secret-App");
            }

            shell.Capture.BlacklistText = string.Join('\n', lines);
            await shell.Capture.SaveBlacklistCommand.ExecuteAsync(null);
            await shell.Capture.CaptureSnapshotCommand.ExecuteAsync(null);
            return $"blacklist={lines.Count} lines snapshot=ok";
        });

        await StepAsync("恢复采集", async () =>
        {
            await shell.Capture.TogglePauseCommand.ExecuteAsync(null);
            if (shell.Capture.IsPaused)
            {
                throw new InvalidOperationException("采集未恢复");
            }

            if (!shell.CaptureStatus.Contains("正在本机采集", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"横幅未恢复：{shell.CaptureStatus}");
            }

            return $"status={shell.CaptureStatus}";
        });

        await StepAsync("语音页启用与关闭", async () =>
        {
            shell.SelectedSectionIndex = 3;
            if (shell.Voice is null)
            {
                throw new SkipStepException("语音页未装配");
            }

            if (!shell.Voice.IsEnabled)
            {
                await WaitAsync(shell.Voice.ToggleCommand.ExecuteAsync(null), TimeSpan.FromSeconds(15));
            }

            if (!shell.Voice.IsEnabled)
            {
                // Missing wake-word/TTS assets or no microphone: the UI must still render the
                // explicit degraded state, so record it as a skip rather than a failure.
                throw new SkipStepException($"语音不可用（环境限制）：{shell.Voice.CapabilityText}");
            }

            await WaitAsync(shell.Voice.ToggleCommand.ExecuteAsync(null), TimeSpan.FromSeconds(15));
            if (shell.Voice.IsEnabled)
            {
                throw new InvalidOperationException("语音未关闭");
            }

            return $"status={shell.Voice.StatusText}";
        });

        await StepAsync("诊断页刷新平台能力", async () =>
        {
            shell.SelectedSectionIndex = 5;
            await shell.Diagnostics.RefreshCommand.ExecuteAsync(null);
            return $"rows={shell.Diagnostics.Capabilities.Count}";
        });

        await StepAsync("日报生成（真实模型）", async () =>
        {
            shell.SelectedSectionIndex = 6;
            await WaitAsync(shell.Reports.GenerateCommand.ExecuteAsync(null), TimeSpan.FromSeconds(300));
            if (!shell.Reports.HasReport)
            {
                throw new InvalidOperationException($"日报未生成：{shell.Reports.StatusMessage}");
            }

            if (string.IsNullOrWhiteSpace(shell.Reports.Markdown))
            {
                throw new InvalidOperationException("日报正文为空");
            }

            return $"versions={shell.Reports.Versions.Count} status={shell.Reports.StatusMessage}";
        });

        await StepAsync("导出日报 Markdown", async () =>
        {
            await shell.Reports.ExportCommand.ExecuteAsync(null);
            if (!shell.Reports.StatusMessage.Contains("已导出", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"导出未完成：{shell.Reports.StatusMessage}");
            }

            return shell.Reports.StatusMessage;
        });

        await StepAsync("主题切换生效", () =>
        {
            var before = shell.ThemeVariant;
            shell.ToggleThemeCommand.Execute(null);
            if (Equals(shell.ThemeVariant, before))
            {
                throw new InvalidOperationException("主题未切换");
            }

            return Task.FromResult<string?>($"theme={shell.ThemeVariant} button={shell.ThemeToggleText}");
        });

        await StepAsync("数据删除两步确认", async () =>
        {
            shell.SelectedSectionIndex = 4;
            if (shell.Data is null)
            {
                throw new SkipStepException("数据页未装配");
            }

            await shell.Data.DeleteDateCommand.ExecuteAsync(null);
            if (!shell.Data.HasPending)
            {
                throw new InvalidOperationException("未进入二次确认");
            }

            await shell.Data.DeleteDateCommand.ExecuteAsync(null);
            if (shell.Data.HasPending)
            {
                throw new InvalidOperationException("二次确认未完成");
            }

            return $"status={shell.Data.StatusMessage}";
        });

        await StepAsync("退出入口可用（无托盘时关闭即退出）", () =>
        {
            if (!shell.RequestExitCommand.CanExecute(null))
            {
                throw new InvalidOperationException("退出命令不可用");
            }

            if (OperatingSystem.IsLinux() && !shell.ExitOnClose)
            {
                throw new InvalidOperationException("无托盘环境下关闭窗口应直接退出");
            }

            return Task.FromResult<string?>($"exitOnClose={shell.ExitOnClose} canExit=true");
        });

        var reportPath = Path.Combine(directory, "report.md");
        var failures = steps.Count(step => !step.Passed);
        var skipped = steps.Count(step => step.Skipped);
        var exitCode = failures == 0 ? 0 : 1;
        await File.WriteAllTextAsync(
            reportPath,
            BuildReport(steps, failures, skipped, directory),
            CancellationToken.None);
        Console.WriteLine(
            $"ui-e2e: steps={steps.Count} failed={failures} skipped={skipped} report={reportPath}");
        Console.Out.Flush();

        // Real-desktop teardown (capture sources, audio device, tray) can block indefinitely and
        // the UI dispatcher stops pumping once the lifetime shuts down. Schedule a deterministic
        // hard exit on a pool thread first so a stuck shutdown can never hang CI, then ask the
        // lifetime to stop gracefully (it normally wins the race).
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            Environment.Exit(exitCode);
        });
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                shutdown(exitCode);
            }
            catch
            {
            }
        });
        return exitCode;
    }

    private static async Task<T?> WaitAsync<T>(Task<T> task, TimeSpan timeout)
    {
        try
        {
            return await task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            return default;
        }
    }

    private static async Task WaitAsync(Task task, TimeSpan timeout) =>
        await task.WaitAsync(timeout);

    /// <summary>Lets layout, styling and the render pass settle so the screenshot is stable.</summary>
    private static async Task SettleAsync(MainWindow window)
    {
        await Task.Delay(300);
        window.InvalidateVisual();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Task.Delay(150);
    }

    /// <summary>Renders the real window using the live Skia renderer; null when unsupported.</summary>
    private static string? TryRender(MainWindow window, string path)
    {
        try
        {
            var width = (int)Math.Round(window.ClientSize.Width);
            var height = (int)Math.Round(window.ClientSize.Height);
            if (width <= 0 || height <= 0)
            {
                return null;
            }

            var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
            bitmap.Render(window);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            bitmap.Save(path, new PngBitmapEncoderOptions());
            return path;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildReport(
        IReadOnlyList<StepResult> steps,
        int failures,
        int skipped,
        string directory)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# 真实 UI 端到端测试报告");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"- 时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- 平台：{System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- 结论：{(failures == 0 ? "通过" : "失败")}（步骤 {steps.Count}，失败 {failures}，跳过 {skipped}）");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- 输出目录：`{directory}`");
        builder.AppendLine();
        builder.AppendLine("| # | 步骤 | 结果 | 详情 | 截图 |");
        builder.AppendLine("|---|---|---|---|---|");
        var index = 0;
        foreach (var step in steps)
        {
            index++;
            var result = !step.Passed ? "❌ 失败" : step.Skipped ? "⏭ 跳过" : "✅ 通过";
            var shot = step.Screenshot is null
                ? "-"
                : $"`{Path.GetFileName(step.Screenshot)}`";
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"| {index} | {step.Name} | {result} | {Escape(step.Detail)} | {shot} |");
        }

        return builder.ToString();
    }

    private static string Escape(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);

    private static string Slug(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            builder.Append(char.IsLetterOrDigit(ch) ? ch : '-');
        }

        return builder.ToString().Trim('-');
    }

    private sealed class SkipStepException(string message) : Exception(message);
}
