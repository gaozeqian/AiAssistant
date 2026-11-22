namespace AiAssistant.Core.Configuration;

/// <summary>
/// Single resolver for the artifacts tree (R5). Nothing else may hardcode the folder name.
/// <para>
/// Layout, each layer with its own retention policy (see docs/guides/artifacts.md):
/// <c>build/</c> publish + staging + sbom, <c>package/</c> installers, <c>cache/</c> external
/// model assets, <c>evidence/</c> test evidence and runtime exports, <c>tmp/</c> scratch.
/// </para>
/// <para>
/// Default root is <c>&lt;current directory&gt;/artifacts</c>, matching the historical behaviour;
/// <see cref="EnvironmentNames.ArtifactsRoot"/> relocates the entire tree in one switch.
/// </para>
/// </summary>
public static class ArtifactPaths
{
    /// <summary>Folder name used when no override is supplied.</summary>
    public const string DefaultRootName = "artifacts";

    /// <summary>Absolute path of the artifacts root for the current process.</summary>
    public static string Root => ResolveRoot();

    /// <summary>Build outputs: <c>dotnet publish</c>, packaging staging, generated SBOM.</summary>
    public static string Build => Resolve("build");

    /// <summary>Distributable installers: zip / msi / pkg / deb / cer / SHA256SUMS.</summary>
    public static string Package => Resolve("package");

    /// <summary>External asset cache (model weights and downloads), rebuildable.</summary>
    public static string Cache => Resolve("cache");

    /// <summary>Test evidence and runtime exports.</summary>
    public static string Evidence => Resolve("evidence");

    /// <summary>Scratch space for tests; callers must clean up after themselves.</summary>
    public static string Tmp => Resolve("tmp");

    /// <summary>
    /// Resolves the artifacts root, honouring <see cref="EnvironmentNames.ArtifactsRoot"/>.
    /// </summary>
    /// <param name="baseDirectory">
    /// Directory the default root is relative to. Defaults to the process working directory;
    /// tests pass the repository root so the location does not depend on how the runner started.
    /// </param>
    public static string ResolveRoot(string? baseDirectory = null)
    {
        var overridden = Environment.GetEnvironmentVariable(EnvironmentNames.ArtifactsRoot);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return Path.GetFullPath(overridden);
        }

        return Path.GetFullPath(Path.Combine(baseDirectory ?? Environment.CurrentDirectory, DefaultRootName));
    }

    /// <summary>Resolves a path under the artifacts root.</summary>
    public static string Resolve(params string[] segments)
    {
        if (segments.Length == 0)
        {
            return Root;
        }

        return Path.Combine([Root, .. segments]);
    }

    /// <summary>Resolves a path under the artifacts root relative to an explicit base directory.</summary>
    public static string Resolve(string baseDirectory, params string[] segments)
    {
        if (segments.Length == 0)
        {
            return ResolveRoot(baseDirectory);
        }

        return Path.Combine([ResolveRoot(baseDirectory), .. segments]);
    }
}
