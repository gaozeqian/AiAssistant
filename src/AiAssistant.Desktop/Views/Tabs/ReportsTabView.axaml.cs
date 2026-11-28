using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AiAssistant.Desktop.Views.Tabs;

public sealed partial class ReportsTabView : TabViewBase
{
    private static readonly TimeSpan CollapseDelay = TimeSpan.FromMilliseconds(150);

    public ReportsTabView()
    {
        InitializeComponent();
        // 两条收起路径（失焦、保存成功）共用同一个善后逻辑：焦点若在被收起的内容里，挪回折叠头。
        ObserveExpansion(DailyScheduleExpander);
        ObserveExpansion(WeeklyScheduleExpander);
    }

    /// <summary>
    /// 焦点离开设置块就收起，避免 660 高下长期占住版本列表的空间。
    /// 150ms 延迟用于跳过"点击 A → 焦点瞬时为空 → 落到 B"的中间态。
    /// </summary>
    private void OnScheduleExpanderLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not Expander expander)
        {
            return;
        }

        DispatcherTimer.RunOnce(() => CollapseIfFocusLeft(expander), CollapseDelay);
    }

    private static void CollapseIfFocusLeft(Expander expander)
    {
        if (!expander.IsExpanded)
        {
            return;
        }

        var focused = TopLevel.GetTopLevel(expander)?.FocusManager?.GetFocusedElement();
        // 焦点丢失（TimePicker 的 Flyout、窗口切换）时不动：此时收起会让弹层悬空。
        if (focused is not Visual visual)
        {
            return;
        }

        // 焦点落进 Flyout / 其它窗口时 TopLevel 不同：收起会让弹层悬空。
        if (TopLevel.GetTopLevel(visual) != TopLevel.GetTopLevel(expander))
        {
            return;
        }

        if (visual.GetVisualAncestors().Contains(expander))
        {
            return;
        }

        expander.IsExpanded = false;
    }

    private static void ObserveExpansion(Expander expander)
    {
        expander.PropertyChanged += (_, args) =>
        {
            if (args.Property != Expander.IsExpandedProperty || args.NewValue is true)
            {
                return;
            }

            var focused = TopLevel.GetTopLevel(expander)?.FocusManager?.GetFocusedElement();
            if (focused is Visual visual && visual.GetVisualAncestors().Contains(expander))
            {
                expander.GetVisualDescendants().OfType<ToggleButton>().FirstOrDefault()?.Focus();
            }
        };
    }
}
