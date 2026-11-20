namespace AiAssistant.Application.Policies;

/// <summary>
/// Evaluates whether an app or window title matches built-in privacy exclusions (password managers, private browsing)
/// or user-configured blacklist patterns (DESIGN.md §12, IMPLEMENTATION.md §2.5).
/// </summary>
public sealed class PrivacyBlacklistPolicy
{
    private static readonly string[] DefaultPatterns =
    [
        "1password",
        "keepass",
        "bitwarden",
        "lastpass",
        "enpass",
        "credential manager",
        "inprivate",
        "incognito",
        "无痕",
        "隐私窗口",
        "密码管理"
    ];

    private string[] _customPatterns = [];

    public IReadOnlyList<string> GetEffectivePatterns() =>
        DefaultPatterns.Concat(_customPatterns)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public void UpdateCustomPatterns(IEnumerable<string> patterns)
    {
        _customPatterns = patterns
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public bool IsExcluded(string? appName, string? windowTitle)
    {
        var patterns = GetEffectivePatterns();
        foreach (var pattern in patterns)
        {
            if ((!string.IsNullOrEmpty(appName) && appName.Contains(pattern, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(windowTitle) && windowTitle.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }
}
