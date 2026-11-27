using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace AiAssistant.Desktop;

/// <summary>
/// Single source of truth for the shell's visual theme. Shared by the production
/// <see cref="ShellApp"/> and the headless UI tests so both render with the same Fluent
/// control themes and semantic brushes; without it the test window has no templates and
/// its visual tree never materializes.
/// </summary>
public static class ShellTheme
{
    public static void Apply(Avalonia.Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        application.Styles.Add(new FluentTheme());
        application.Resources.ThemeDictionaries[ThemeVariant.Light] = BuildThemeDictionary(isDark: false);
        application.Resources.ThemeDictionaries[ThemeVariant.Dark] = BuildThemeDictionary(isDark: true);
        // 令牌与主题无关，放在应用级资源里，XAML 通过 {DynamicResource FontSizeBody} 等引用。
        DesignTokens.AddTo(application.Resources);
    }

    /// <summary>
    /// Semantic brushes keyed by theme variant, referencing the VS Code Dark Modern / Light Modern
    /// palettes (neutral grays + #0078D4 accent). Controls reference them with {DynamicResource} so
    /// the whole window restyles when the theme toggles.
    /// </summary>
    public static ResourceDictionary BuildThemeDictionary(bool isDark)
    {
        var d = new ResourceDictionary();
        void Add(string key, string hex) => d[key] = new SolidColorBrush(Color.Parse(hex));

        if (!isDark)
        {
            // Light Modern
            Add("PageBackgroundBrush", "#F3F3F3");
            Add("CardBackgroundBrush", "#FFFFFF");
            Add("HeaderBackgroundBrush", "#EEEEEE");
            Add("HeaderBorderBrush", "#D4D4D4");
            Add("HeaderTextBrush", "#1F1F1F");
            Add("HeaderTextSecondaryBrush", "#616161");
            Add("HeaderBadgeBackgroundBrush", "#E5E5E5");
            Add("HeaderBadgeForegroundBrush", "#242424");
            Add("AccentBrush", "#0078D4");
            Add("AccentTextBrush", "#005A9E");
            Add("BadgeForegroundBrush", "#005A9E");
            Add("AccentSoftBackgroundBrush", "#D6EBFF");
            Add("ListSelectionBackgroundBrush", "#CCE4FF");
            Add("TextPrimaryBrush", "#1F1F1F");
            Add("InputBackgroundBrush", "#FFFFFF");
            Add("InputBorderBrush", "#D4D4D4");
            // 主按钮三态：Fluent 模板在 hover/pressed 时改的是模板内 ContentPresenter，
            // 只设 Button.Background 会被覆盖成 10% 黑/纯黑，白字直接看不见。
            Add("OnAccentBrush", "#FFFFFF");
            Add("AccentHoverBrush", "#0A6CBF");
            Add("AccentPressedBrush", "#095A9E");
            Add("AccentDisabledBrush", "#C7E0F4");
            Add("OnAccentDisabledBrush", "#6E6E6E");
            // 输入框聚焦/悬停默认吃 Fluent 的 #000000（暗色）与纯白（亮色），与卡片底色不一致。
            Add("TextControlBackgroundFocused", "#FFFFFF");
            Add("TextControlBackgroundPointerOver", "#FFFFFF");
            Add("ComboBoxBackgroundFocused", "#FFFFFF");
            Add("ComboBoxBackgroundPointerOver", "#FFFFFF");
            Add("DatePickerFlyoutPresenterBackground", "#FFFFFF");
            Add("DatePickerFlyoutPresenterBorderBrush", "#C8C8C8");
            Add("MutedBrush", "#616161");
            Add("SubtleCardBrush", "#F3F3F3");
            Add("PlaceholderBrush", "#E5E5E5");
            Add("DividerBrush", "#D4D4D4");
            Add("SuccessForegroundBrush", "#22863A");
            Add("SuccessBackgroundBrush", "#E6F4EA");
            Add("DangerForegroundBrush", "#A1260D");
            Add("DangerBackgroundBrush", "#FBE9E7");
            Add("WarningForegroundBrush", "#895503");
            Add("WarningBackgroundBrush", "#FFF4CE");
        }
        else
        {
            // Dark Modern
            Add("PageBackgroundBrush", "#181818");
            Add("CardBackgroundBrush", "#1F1F1F");
            Add("HeaderBackgroundBrush", "#181818");
            Add("HeaderBorderBrush", "#2D2D2D");
            Add("HeaderTextBrush", "#D4D4D4");
            Add("HeaderTextSecondaryBrush", "#9D9D9D");
            Add("HeaderBadgeBackgroundBrush", "#2D2D2D");
            Add("HeaderBadgeForegroundBrush", "#E0E0E0");
            Add("AccentBrush", "#0078D4");
            Add("AccentTextBrush", "#4CA6E8");
            Add("BadgeForegroundBrush", "#7CC4F5");
            Add("AccentSoftBackgroundBrush", "#123B63");
            Add("ListSelectionBackgroundBrush", "#04395E");
            Add("TextPrimaryBrush", "#D4D4D4");
            Add("InputBackgroundBrush", "#252526");
            Add("InputBorderBrush", "#3C3C3C");
            Add("OnAccentBrush", "#FFFFFF");
            Add("AccentHoverBrush", "#0A6CBF");
            Add("AccentPressedBrush", "#095A9E");
            Add("AccentDisabledBrush", "#1E3A54");
            Add("OnAccentDisabledBrush", "#8C8C8C");
            Add("TextControlBackgroundFocused", "#252526");
            Add("TextControlBackgroundPointerOver", "#252526");
            Add("ComboBoxBackgroundFocused", "#252526");
            Add("ComboBoxBackgroundPointerOver", "#252526");
            Add("DatePickerFlyoutPresenterBackground", "#252526");
            Add("DatePickerFlyoutPresenterBorderBrush", "#3F3F3F");
            Add("MutedBrush", "#A8A8A8");
            Add("SubtleCardBrush", "#252526");
            Add("PlaceholderBrush", "#2D2D2D");
            Add("DividerBrush", "#3C3C3C");
            Add("SuccessForegroundBrush", "#89D185");
            Add("SuccessBackgroundBrush", "#1E3B2A");
            Add("DangerForegroundBrush", "#F48771");
            Add("DangerBackgroundBrush", "#3B1E1E");
            Add("WarningForegroundBrush", "#CCA700");
            Add("WarningBackgroundBrush", "#3B3410");
        }

        return d;
    }
}
