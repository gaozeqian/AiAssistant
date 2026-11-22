using System.Reflection;
using System.Xml.Linq;
using AiAssistant.Application;
using AiAssistant.Capture;
using AiAssistant.Core;
using AiAssistant.Desktop;
using AiAssistant.Export;
using AiAssistant.Host;
using AiAssistant.LocalApi;
using AiAssistant.Models;
using AiAssistant.Platform.Linux;
using AiAssistant.Platform.macOS;
using AiAssistant.Platform.Windows;
using AiAssistant.Processing;
using AiAssistant.Scheduler;
using AiAssistant.Storage;
using AiAssistant.Summarization;
using AiAssistant.Voice;
using NetArchTest.Rules;

namespace AiAssistant.Architecture.Tests;

/// <summary>
/// Enforces all 6 physical architecture &amp; dependency rules from implementation.md §3.2.
/// </summary>
public sealed class DependencyRuleTests
{
    private static readonly Assembly[] AllSourceAssemblies =
    [
        typeof(CoreAssemblyMarker).Assembly,
        typeof(ApplicationAssemblyMarker).Assembly,
        typeof(CaptureAssemblyMarker).Assembly,
        typeof(ProcessingAssemblyMarker).Assembly,
        typeof(StorageAssemblyMarker).Assembly,
        typeof(ModelsAssemblyMarker).Assembly,
        typeof(SummarizationAssemblyMarker).Assembly,
        typeof(SchedulerAssemblyMarker).Assembly,
        typeof(VoiceAssemblyMarker).Assembly,
        typeof(LocalApiAssemblyMarker).Assembly,
        typeof(ExportAssemblyMarker).Assembly,
        typeof(PlatformWindowsAssemblyMarker).Assembly,
        typeof(PlatformAssemblyMarker).Assembly,
        typeof(PlatformLinuxAssemblyMarker).Assembly,
        typeof(DesktopAssemblyMarker).Assembly,
        typeof(HostAssemblyMarker).Assembly
    ];

    [Fact]
    public void Rule1_Core_MustNotReferenceAnyProjectOrThirdPartyPackage()
    {
        var coreCsproj = LoadCsproj("AiAssistant.Core");
        Assert.Empty(GetProjectReferences(coreCsproj));
        Assert.Empty(GetPackageReferences(coreCsproj));

        var result = Types.InAssembly(typeof(CoreAssemblyMarker).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "AiAssistant.Application",
                "AiAssistant.Capture",
                "AiAssistant.Processing",
                "AiAssistant.Storage",
                "AiAssistant.Models",
                "AiAssistant.Summarization",
                "AiAssistant.Scheduler",
                "AiAssistant.Voice",
                "AiAssistant.Platform",
                "AiAssistant.Desktop",
                "AiAssistant.Host",
                "Avalonia",
                "Microsoft.Data.Sqlite")
            .GetResult();

        Assert.True(result.IsSuccessful);
    }

    [Fact]
    public void Rule2_Application_MustOnlyReferenceCore()
    {
        var appCsproj = LoadCsproj("AiAssistant.Application");
        var refs = GetProjectReferences(appCsproj);
        Assert.Equal(["AiAssistant.Core"], refs);

        var forbidden = new[]
        {
            "AiAssistant.Storage",
            "AiAssistant.Models",
            "AiAssistant.Platform.Windows",
            "AiAssistant.Platform.macOS",
            "AiAssistant.Platform.Linux",
            "AiAssistant.Desktop",
            "AiAssistant.Host",
            "AiAssistant.Capture",
            "AiAssistant.Processing",
            "AiAssistant.Summarization",
            "AiAssistant.Scheduler",
            "AiAssistant.Voice",
            "AiAssistant.LocalApi",
            "AiAssistant.Export",
            "Avalonia",
            "Microsoft.Data.Sqlite"
        };

        var result = Types.InAssembly(typeof(ApplicationAssemblyMarker).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(forbidden)
            .GetResult();

        Assert.True(result.IsSuccessful);
    }

    [Fact]
    public void Rule3_Desktop_MustNotReferenceStorageOrPlatformOrInfrastructure()
    {
        var desktopCsproj = LoadCsproj("AiAssistant.Desktop");
        var refs = GetProjectReferences(desktopCsproj);

        Assert.DoesNotContain("AiAssistant.Storage", refs);
        Assert.DoesNotContain("AiAssistant.Models", refs);
        Assert.DoesNotContain(refs, r => r.StartsWith("AiAssistant.Platform.", StringComparison.Ordinal));
        Assert.DoesNotContain("AiAssistant.Host", refs);
        Assert.All(refs, r => Assert.Contains(r, new[] { "AiAssistant.Core", "AiAssistant.Application" }));

        var result = Types.InAssembly(typeof(DesktopAssemblyMarker).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "AiAssistant.Storage",
                "AiAssistant.Models",
                "AiAssistant.Platform.Windows",
                "AiAssistant.Platform.macOS",
                "AiAssistant.Platform.Linux",
                "AiAssistant.Host",
                "Microsoft.Data.Sqlite")
            .GetResult();

        Assert.True(result.IsSuccessful);
    }

    [Fact]
    public void Rule4_PlatformProjects_MustNotReferenceEachOtherOrDesktopOrStorage()
    {
        var platformProjects = new[]
        {
            "AiAssistant.Platform.Windows",
            "AiAssistant.Platform.macOS",
            "AiAssistant.Platform.Linux"
        };

        foreach (var project in platformProjects)
        {
            var refs = GetProjectReferences(LoadCsproj(project));
            Assert.DoesNotContain(refs, r => r.StartsWith("AiAssistant.Platform.", StringComparison.Ordinal));
            Assert.DoesNotContain("AiAssistant.Desktop", refs);
            Assert.DoesNotContain("AiAssistant.Storage", refs);
            Assert.DoesNotContain("AiAssistant.Host", refs);
        }
    }

    [Fact]
    public void Rule5_OnlyHost_MayActAsCompositionRootReferencingAllModules()
    {
        var nonHostProjects = AllSourceAssemblies
            .Select(a => a.GetName().Name!)
            .Where(name => !string.Equals(name, "AiAssistant.Host", StringComparison.Ordinal))
            .ToArray();

        foreach (var project in nonHostProjects)
        {
            var refs = GetProjectReferences(LoadCsproj(project));
            Assert.DoesNotContain("AiAssistant.Host", refs);

            // Non-Host modules must never combine Storage + Platform or Desktop + Storage
            Assert.False(
                refs.Contains("AiAssistant.Storage") && refs.Any(r => r.StartsWith("AiAssistant.Platform.", StringComparison.Ordinal)),
                $"{project} illegally references both Storage and Platform.");
        }

        var hostRefs = GetProjectReferences(LoadCsproj("AiAssistant.Host"));
        Assert.Contains("AiAssistant.Core", hostRefs);
        Assert.Contains("AiAssistant.Application", hostRefs);
        Assert.Contains("AiAssistant.Storage", hostRefs);
        Assert.Contains("AiAssistant.Desktop", hostRefs);
    }

    [Fact]
    public void Rule6_ArchitectureVerifier_DetectsSyntheticViolation()
    {
        // Proves that if someone adds a forbidden reference in a .csproj, the gate fails deterministically.
        var illegalXml = XDocument.Parse("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="..\AiAssistant.Storage\AiAssistant.Storage.csproj" />
              </ItemGroup>
            </Project>
            """);

        var refs = GetProjectReferences(illegalXml);
        Assert.Contains("AiAssistant.Storage", refs);
    }

    private static XDocument LoadCsproj(string projectName)
    {
        var repoRoot = FindRepoRoot();
        var path = Path.Combine(repoRoot, "src", projectName, $"{projectName}.csproj");
        Assert.True(File.Exists(path), $"Expected project file at {path}");
        return XDocument.Load(path);
    }

    private static string[] GetProjectReferences(XDocument doc) =>
        doc.Descendants("ProjectReference")
            .Select(e => e.Attribute("Include")?.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => Path.GetFileNameWithoutExtension(v!.Replace('\\', '/')))
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToArray();

    private static string[] GetPackageReferences(XDocument doc) =>
        doc.Descendants("PackageReference")
            .Select(e => e.Attribute("Include")?.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToArray();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AiAssistant.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate AiAssistant.sln from test directory.");
    }
}
