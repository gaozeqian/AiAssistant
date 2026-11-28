using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AiAssistant.Desktop;

/// <summary>
/// UI design tokens: type scale, spacing, corner radius and motion duration. <see cref="ShellTheme"/>
/// injects them into application resources so XAML can use <c>{DynamicResource FontSizeBody}</c>;
/// code uses the constants directly. One edit changes every screen.
/// </summary>
public static class DesignTokens
{
    /// <summary>
    /// Six-step type scale. 12 is the body size the whole shell is built around; 18 is reserved for
    /// the brand mark in the header, 20 for screen titles, 24 for Markdown H1 only.
    /// </summary>
    public static class FontSize
    {
        public const double Caption = 11;
        public const double Body = 12;
        public const double Label = 13;
        public const double Title = 15;
        public const double Brand = 18;
        public const double Page = 20;
        public const double Document = 24;
    }

    /// <summary>4/8 spacing scale. Cards use Lg (compact) or Xl (standard); nested blocks use Md.</summary>
    public static class Space
    {
        public const double Xs = 4;
        public const double Sm = 8;
        public const double Md = 12;
        public const double Lg = 16;
        public const double Xl = 24;

        /// <summary>
        /// 覆盖式滚动条宽度（Fluent ScrollBarSize=16）加 2px 安全间隙。诊断页与数据页的根 Grid
        /// 用这个值给滚动条让位；headless 用例断言按钮右缘不得被滚动条压住。
        /// </summary>
        public const double ScrollGutter = 18;
    }

    /// <summary>Three tiers for surfaces plus Xs for sub-20px elements (spinner arrows, bars).</summary>
    public static class Radius
    {
        public const double Xs = 3;
        public const double Sm = 6;
        public const double Md = 10;
        public const double Lg = 14;
    }

    /// <summary>Maximum width of long-form reading panes (event detail, report body).</summary>
    public const double ReadingMaxWidth = 920;

    /// <summary>Motion durations; zeroed when the OS asks for reduced motion.</summary>
    public static class Motion
    {
        public const int FastMs = 120;
        public const int BaseMs = 160;
        public const int SlowMs = 200;
    }

    /// <summary>Registers every token as an application resource. Called once from <see cref="ShellTheme.Apply"/>.</summary>
    /// <param name="resources">Target dictionary (the application's resource dictionary).</param>
    /// <param name="reducedMotion">When true every motion duration becomes zero.</param>
    public static void AddTo(IResourceDictionary resources, bool reducedMotion = false)
    {
        ArgumentNullException.ThrowIfNull(resources);

        resources["FontSizeCaption"] = FontSize.Caption;
        resources["FontSizeBody"] = FontSize.Body;
        resources["FontSizeLabel"] = FontSize.Label;
        resources["FontSizeTitle"] = FontSize.Title;
        resources["FontSizeBrand"] = FontSize.Brand;
        resources["FontSizePage"] = FontSize.Page;
        resources["FontSizeDocument"] = FontSize.Document;

        // Spacing 属性是 double，Padding/Margin 是 Thickness，两套键分开。
        resources["SpacingXs"] = Space.Xs;
        resources["SpacingSm"] = Space.Sm;
        resources["SpacingMd"] = Space.Md;
        resources["SpacingLg"] = Space.Lg;
        resources["SpacingXl"] = Space.Xl;

        resources["SpaceXs"] = new Thickness(Space.Xs);
        resources["SpaceSm"] = new Thickness(Space.Sm);
        resources["SpaceMd"] = new Thickness(Space.Md);
        resources["SpaceLg"] = new Thickness(Space.Lg);
        resources["SpaceXl"] = new Thickness(Space.Xl);
        resources["ScrollGutterMargin"] = new Thickness(0, 0, Space.ScrollGutter, 0);

        resources["RadiusXs"] = new CornerRadius(Radius.Xs);
        resources["RadiusSm"] = new CornerRadius(Radius.Sm);
        resources["RadiusMd"] = new CornerRadius(Radius.Md);
        resources["RadiusLg"] = new CornerRadius(Radius.Lg);

        resources["ReadingMaxWidth"] = ReadingMaxWidth;

        resources["DurationFast"] = TimeSpan.FromMilliseconds(reducedMotion ? 0 : Motion.FastMs);
        resources["DurationBase"] = TimeSpan.FromMilliseconds(reducedMotion ? 0 : Motion.BaseMs);
        resources["DurationSlow"] = TimeSpan.FromMilliseconds(reducedMotion ? 0 : Motion.SlowMs);
    }
}
