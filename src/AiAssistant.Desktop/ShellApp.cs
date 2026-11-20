using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace AiAssistant.Desktop;

public sealed class ShellApp : Avalonia.Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new Window
            {
                Title = "AI 个人活动追踪助手",
                Width = 1120,
                Height = 720,
                Content = new TextBlock
                {
                    Text = "AI 个人活动追踪助手 · 壳程序已就绪"
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
