namespace AiAssistant.Models;

/// <summary>
/// Single mapping from the user-facing thinking switch onto the OpenAI-compatible
/// <c>reasoning_effort</c> field, shared by the backend and the capability prober so both agree.
/// Thinking models otherwise spend the output budget on reasoning and return empty content.
/// A disabled switch is only sent to loopback runtimes: cloud providers reject unknown
/// parameters with HTTP 400 and may reason by default, so they are left untouched.
/// </summary>
internal static class ReasoningEffortPolicy
{
    public static string? Resolve(bool think, string? thinkingLevel, Uri baseAddress)
    {
        if (think)
        {
            return string.IsNullOrWhiteSpace(thinkingLevel) ? "high" : thinkingLevel;
        }

        return baseAddress.IsLoopback ? "none" : null;
    }
}
