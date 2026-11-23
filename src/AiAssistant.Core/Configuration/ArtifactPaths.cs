namespace AiAssistant.Core.Configuration;

/// <summary>
/// Single resolver for the artifacts tree. Nothing else may hardcode the folder name.
/// <para>
/// Layout (see docs/development/conventions-and-artifacts.md):
/// <c>bin/</c> and <c>obj/</c> build intermediates (including <c>obj/native/</c> SwiftPM scratch),
/// <c>publish/&lt;rid&gt;/</c> publish output and native manifests,
/// <c>staging/&lt;rid&gt;-&lt;format&gt;/</c> installer assembly,
/// <c>dist/&lt;version&gt;/</c> final distributables,
/// <c>cache/models/</c> external model assets, <c>test-results/</c> test evidence,
/// <c>tmp/</c> scratch.
/// </para>
/// <para>
/// Default root is <c>&lt;repository root&gt;/artifacts</c>: the resolver walks up from the
/// application base directory and then the current directory until it finds <c>AiAssistant.sln</c>.
/// <see cref="EnvironmentNames.ArtifactsRoot"/> relocates the entire tree in one switch.
/// </para>
/// </summary>
public static class ArtifactPaths
{
    /// <summary>Folder name used when no override is supplied.</summary>
    public const string DefaultRootName = "artifacts";

    /// <summary>Absolute path of the artifacts root for the current process.</summary>
    public static string Root => ResolveRoot();

    /// <summary>Managed build outputs (<c>dotnet build</c>).</summary>
    public static string Bin => Resolve("bin");

    /// <summary>Intermediate outputs, including the SwiftPM scratch directory.</summary>
    public static string Obj => Resolve("obj");

    /// <summary>Publish output; each RID gets its own folder plus a native dependency manifest.</summary>
    public static string Publish => Resolve("publish");

    /// <summary>Per-format installer assembly area.</summary>
    public static string Staging => Resolve("staging");

    /// <summary>Final distributables for one version.</summary>
    public static string Dist => Resolve("dist");

    /// <summary>External asset cache (model weights and downloads), rebuildable.</summary>
    public static string Cache => Resolve("cache");

    /// <summary>MTP results, coverage, E2E screenshots, benchmarks, soak and golden evidence.</summary>
    public static string TestResults => Resolve("test-results");

    /// <summary>Scratch space for tests; callers must clean up after themselves.</summary>
    public static string Tmp => Resolve("tmp");

    /// <summary>
    /// Resolves the artifacts root, honouring <see cref="EnvironmentNames.ArtifactsRoot"/>.
    /// </summary>
    /// <param name="baseDirectory">
    /// Directory the default root is relative to. Defaults to the detected repository root;
    /// tests pass the repository root so the location does not depend on how the runner started.
    /// </param>
    public static string ResolveRoot(string? baseDirectory = null)
    {
        var overridden = Environment.GetEnvironmentVariable(EnvironmentNames.ArtifactsRoot);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return Path.GetFullPath(overridden);
        }

        if (!string.IsNullOrWhiteSpace(baseDirectory))
        {
            return Path.GetFullPath(Path.Combine(baseDirectory, DefaultRootName));
        }

        // Probe upwards instead of trusting the current directory: running a tool from a
        // subdirectory must not create a nested artifacts/ tree inside the source tree.
        var repositoryRoot =
            FindRepositoryRoot(AppContext.BaseDirectory) ??
            FindRepositoryRoot(Environment.CurrentDirectory);
        return Path.GetFullPath(Path.Combine(repositoryRoot ?? Environment.CurrentDirectory, DefaultRootName));
    }

    private static string? FindRepositoryRoot(string start)
    {
        DirectoryInfo? directory;
        try
        {
            directory = new DirectoryInfo(start);
        }
        catch (ArgumentException)
        {
            return null;
        }

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AiAssistant.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
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
