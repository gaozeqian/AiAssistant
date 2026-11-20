using System.Text.RegularExpressions;
using AiAssistant.Application.Ports;

namespace AiAssistant.Processing;

public sealed record RedactionResult(string Text, int RedactionCount);

/// <summary>
/// Deterministic local redaction applied before persistence or any model call.
/// Covers credentials, tokens, private keys, e-mail/phone identifiers and long secret-like strings.
/// </summary>
public sealed partial class SensitiveDataRedactor : ITextRedactor
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(120);
    private readonly IReadOnlyList<Regex> _customPatterns;

    TextRedaction ITextRedactor.Redact(string input)
    {
        var result = Redact(input);
        return new TextRedaction(result.Text, result.RedactionCount);
    }

    public SensitiveDataRedactor(IEnumerable<string>? customPatterns = null)
    {
        _customPatterns = (customPatterns ?? [])
            .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
            .Select(pattern => new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant, RegexTimeout))
            .ToArray();
    }

    public RedactionResult Redact(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return new RedactionResult(string.Empty, 0);
        }

        var text = input;
        var count = 0;
        foreach (var (pattern, replacement) in BuiltInRules())
        {
            text = pattern.Replace(text, match =>
            {
                count++;
                return match.Groups.Count > 1
                    ? replacement.Replace("$1", match.Groups[1].Value, StringComparison.Ordinal)
                    : replacement;
            });
        }

        foreach (var custom in _customPatterns)
        {
            try
            {
                text = custom.Replace(text, match =>
                {
                    count++;
                    return "[REDACTED]";
                });
            }
            catch (RegexMatchTimeoutException)
            {
                // A pathological user pattern must never block persistence.
            }
        }

        return new RedactionResult(text, count);
    }

    private static IEnumerable<(Regex Pattern, string Replacement)> BuiltInRules()
    {
        yield return (CredentialAssignmentRegex(), "$1=[REDACTED]");
        yield return (BearerTokenRegex(), "Bearer [REDACTED]");
        yield return (KnownApiKeyRegex(), "[REDACTED_KEY]");
        yield return (PrivateKeyBlockRegex(), "[REDACTED_PRIVATE_KEY]");
        yield return (EmailRegex(), "[REDACTED_EMAIL]");
        yield return (ChinaMobileRegex(), "[REDACTED_PHONE]");
        yield return (ChinaIdCardRegex(), "[REDACTED_ID]");
        yield return (LongSecretRegex(), "[REDACTED_SECRET]");
    }

    [GeneratedRegex(
        @"(?i)\b(password|passwd|pwd|secret|api[_-]?key|access[_-]?token|refresh[_-]?token)\b\s*[:=]\s*(?:""[^""\r\n]*""|'[^'\r\n]*'|[^\s,;]+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex CredentialAssignmentRegex();

    [GeneratedRegex(@"(?i)\bBearer\s+[A-Za-z0-9._~+/\-]{12,}=*", RegexOptions.CultureInvariant)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(@"\b(?:sk|ghp|gho|ghs|xox[baprs])[-_][A-Za-z0-9_\-]{16,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex KnownApiKeyRegex();

    [GeneratedRegex(
        @"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----",
        RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyBlockRegex();

    [GeneratedRegex(@"\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"(?<!\d)1[3-9]\d{9}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex ChinaMobileRegex();

    [GeneratedRegex(@"(?<!\d)\d{17}[\dXx](?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex ChinaIdCardRegex();

    [GeneratedRegex(@"\b[A-Fa-f0-9]{32,}\b|\b[A-Za-z0-9+/]{40,}={0,2}\b", RegexOptions.CultureInvariant)]
    private static partial Regex LongSecretRegex();
}
