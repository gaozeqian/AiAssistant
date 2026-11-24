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
            Add("AccentSoftBackgroundBrush", "#D6EBFF");
            Add("ListSelectionBackgroundBrush", "#CCE4FF");
            Add("TextPrimaryBrush", "#1F1F1F");
            Add("InputBackgroundBrush", "#FFFFFF");
            Add("InputBorderBrush", "#D4D4D4");
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
            Add("AccentSoftBackgroundBrush", "#123B63");
            Add("ListSelectionBackgroundBrush", "#04395E");
            Add("TextPrimaryBrush", "#D4D4D4");
            Add("InputBackgroundBrush", "#252526");
            Add("InputBorderBrush", "#3C3C3C");
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
