namespace AiAssistant.Desktop.ViewModels;

/// <summary>
/// Tab positions in the main shell, ordered by usage frequency: daily surfaces first, one-off
/// diagnostics last. Navigation code and tests reference these constants instead of magic numbers,
/// so reordering the tabs is a one-line change here plus the XAML order.
/// </summary>
public static class ShellSection
{
    public const int Timeline = 0;
    public const int Search = 1;
    public const int Reports = 2;
    public const int Capture = 3;
    public const int Voice = 4;
    public const int Data = 5;
    public const int Diagnostics = 6;
}
