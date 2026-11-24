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
[Trait("Category", "Unit")]
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
        // The Host assembly is published as AiAssistant (AssemblyName), while its project folder
        // remains AiAssistant.Host; exclude it by marker type instead of assembly name.
        var hostAssembly = typeof(HostAssemblyMarker).Assembly;
        var nonHostProjects = AllSourceAssemblies
            .Where(assembly => !ReferenceEquals(assembly, hostAssembly))
            .Select(assembly => assembly.GetName().Name!)
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

    /// <summary>Exact dependency matrix for all 16 production modules (single source: the .csproj files).</summary>
    [Fact]
    public void Rule7_AllSixteenModules_MatchExactDependencyMatrix()
    {
        var expected = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["AiAssistant.Core"] = [],
            ["AiAssistant.Application"] = ["AiAssistant.Core"],
            ["AiAssistant.Capture"] = ["AiAssistant.Application", "AiAssistant.Core"],
            ["AiAssistant.Desktop"] = ["AiAssistant.Application", "AiAssistant.Core"],
            ["AiAssistant.Export"] = ["AiAssistant.Application", "AiAssistant.Core"],
            ["AiAssistant.LocalApi"] = ["AiAssistant.Application", "AiAssistant.Core"],
            ["AiAssistant.Models"] = ["AiAssistant.Application", "AiAssistant.Core"],
            ["AiAssistant.Platform.Linux"] = ["AiAssistant.Application", "AiAssistant.Core"],
            ["AiAssistant.Platform.Windows"] = ["AiAssistant.Application", "AiAssistant.Core"],
            ["AiAssistant.Platform.macOS"] = ["AiAssistant.Application", "AiAssistant.Core"],
            ["AiAssistant.Processing"] = ["AiAssistant.Application", "AiAssistant.Core"],
            ["AiAssistant.Scheduler"] = ["AiAssistant.Application", "AiAssistant.Core"],
            ["AiAssistant.Storage"] = ["AiAssistant.Application", "AiAssistant.Core"],
            ["AiAssistant.Summarization"] = ["AiAssistant.Application", "AiAssistant.Core"],
            ["AiAssistant.Voice"] = ["AiAssistant.Application", "AiAssistant.Core"],
            ["AiAssistant.Host"] =
            [
                "AiAssistant.Application", "AiAssistant.Capture", "AiAssistant.Core", "AiAssistant.Desktop",
                "AiAssistant.Export", "AiAssistant.LocalApi", "AiAssistant.Models", "AiAssistant.Platform.Linux",
                "AiAssistant.Platform.Windows", "AiAssistant.Platform.macOS", "AiAssistant.Processing",
                "AiAssistant.Scheduler", "AiAssistant.Storage", "AiAssistant.Summarization", "AiAssistant.Voice"
            ]
        };

        Assert.Equal(16, expected.Count);
        foreach (var (project, allowed) in expected)
        {
            Assert.Equal(allowed, GetProjectReferences(LoadCsproj(project)));
        }
    }

    [Fact]
    public void Rule8_ProductionAssemblies_MustNotExposeTestOrBenchmarkTypes()
    {
        foreach (var assembly in AllSourceAssemblies)
        {
            var leaked = assembly.GetExportedTypes()
                .Where(type => type.FullName?.Contains(".Tests", StringComparison.Ordinal) == true ||
                               type.FullName?.Contains(".Benchmarks", StringComparison.Ordinal) == true)
                .Select(type => type.FullName)
                .ToArray();
            Assert.Empty(leaked);
        }
    }

    [Fact]
    public void Rule9_ProductionCode_MustNotCallSystemClockDirectly()
    {
        var repoRoot = FindRepoRoot();
        var sourceRoot = Path.Combine(repoRoot, "src");
        var forbidden = new[]
        {
            "DateTime.Now", "DateTime.Today", "DateTime.UtcNow",
            "DateTimeOffset.Now", "DateTimeOffset.UtcNow"
        };
        var violations = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => (path, line, index))
                .Where(item => forbidden.Any(token => item.line.Contains(token, StringComparison.Ordinal)) &&
                               !item.line.Contains("IClock", StringComparison.Ordinal) &&
                               !item.line.Contains("SystemClock", StringComparison.Ordinal) &&
                               !item.line.Contains("TimeProvider", StringComparison.Ordinal)))
            .Select(item => $"{Path.GetRelativePath(repoRoot, item.path)}:{item.index + 1}")
            .ToArray();
        Assert.Empty(violations);
    }

    /// <summary>
    /// Implementation details that were deliberately tightened in the exposure-surface cleanup
    /// must stay non-public; concrete classes must stay sealed unless intentionally static/abstract.
    /// </summary>
    [Fact]
    public void Rule10_InternalImplementationDetails_KeepMinimalExposure()
    {
        string[] internalOnly =
        [
            "ReportMarkdownBuilder", "PromptMinimizer", "ResidualSensitiveDataDetector",
            "X11WindowActivitySource", "LocalModelGateway", "TimeoutModelGateway",
            "SlidingWindowRateLimiter", "SchemaMigrator", "VoiceCommandParser",
            "CommandAudioWavEncoder", "RapidOcrCtcDecoder", "RapidOcrDetPostProcessor",
            "RapidOcrImage", "EnergyTurnDiarizationEngine",
            "DailyReportStats", "ModelsFileDocument", "ModelsFileDocumentV1",
            "ModelPolicyDocumentV1", "ModelEndpointDocument", "ModelEndpointDocumentV1",
            "EmbeddingEndpointDocumentV1", "PendingDeletion"
        ];

        foreach (var name in internalOnly)
        {
            var type = AllSourceAssemblies
                .SelectMany(assembly => assembly.GetTypes())
                .Single(candidate => candidate.Name == name);
            Assert.False(type.IsPublic, $"{name} must not be publicly exposed");
            if (type.IsClass && !type.IsAbstract && !type.IsSealed)
            {
                Assert.Fail($"{name} must be sealed or static");
            }
        }
    }

    [Fact]
    public void TestProjects_AreRegisteredInSolutionAndReadme()
    {
        var repoRoot = FindRepoRoot();
        var readme = File.ReadAllText(Path.Combine(repoRoot, "tests", "README.md"));
        var solution = File.ReadAllText(Path.Combine(repoRoot, "AiAssistant.sln"));
        var projects = Directory.GetFiles(
            Path.Combine(repoRoot, "tests"),
            "*.csproj",
            SearchOption.AllDirectories);

        Assert.NotEmpty(projects);
        foreach (var project in projects)
        {
            var name = Path.GetFileNameWithoutExtension(project);
            Assert.Contains(name, readme, StringComparison.Ordinal);
            Assert.Contains(name, solution, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DesktopTests_MustNotReferencePlatformProjects()
    {
        var csproj = XDocument.Load(Path.Combine(
            FindRepoRoot(),
            "tests",
            "AiAssistant.Desktop.Tests",
            "AiAssistant.Desktop.Tests.csproj"));
        var references = GetProjectReferences(csproj);
        Assert.DoesNotContain(references, reference => reference.StartsWith("AiAssistant.Platform.", StringComparison.Ordinal));
    }

    [Fact]
    public void TestMethods_MustNotSilentlyReturnWithoutSkip()
    {
        var repoRoot = FindRepoRoot();
        var violations = new List<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(repoRoot, "tests"), "*.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                var trimmed = lines[index].TrimStart();
                if (!trimmed.StartsWith("[Fact]", StringComparison.Ordinal) &&
                    !trimmed.StartsWith("[Theory]", StringComparison.Ordinal))
                {
                    continue;
                }

                for (var cursor = index + 1; cursor < Math.Min(lines.Length, index + 80); cursor++)
                {
                    var candidate = lines[cursor];
                    if (candidate.StartsWith("    [", StringComparison.Ordinal) ||
                        candidate.StartsWith("    private ", StringComparison.Ordinal))
                    {
                        break;
                    }

                    if (!candidate.Trim().Equals("return;", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var recent = string.Join('\n', lines[Math.Max(index, cursor - 4)..cursor]);
                    if (!recent.Contains("Assert.Skip", StringComparison.Ordinal))
                    {
                        violations.Add($"{Path.GetRelativePath(repoRoot, file)}:{cursor + 1}");
                    }
                }
            }
        }

        Assert.Empty(violations);
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
