namespace AiAssistant.Core.Configuration;

/// <summary>
/// Every environment variable the product reads, in one place (R9).
/// Code must reference these constants instead of string literals so the audit can prove
/// the variable set is closed and documented in docs/reference/environment-variables.md.
/// </summary>
public static class EnvironmentNames
{
    /// <summary>Relocates the whole artifacts tree; empty means "&lt;current directory&gt;/artifacts".</summary>
    public const string ArtifactsRoot = "AIASSISTANT_ARTIFACTS_ROOT";
}
