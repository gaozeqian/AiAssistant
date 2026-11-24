using System.Diagnostics;
using AiAssistant.Core.Configuration;
using AiAssistant.Testing.Common;

namespace AiAssistant.Host.Tests;

[Collection(ProcessEnvironmentCollectionName.Value)]
[Trait("Category", "Integration")]
public sealed class HostExitCodeTests
{
    [Theory]
    [InlineData("--smoke-check", 0)]
    [InlineData("--help", 0)]
    [InlineData("--version", 0)]
    public async Task InformationalCommands_ExitZero(string argument, int expected)
    {
        using var directory = new TempArtifactDirectory("host-exit");
        var result = await RunHostAsync(directory.Path, argument);
        Assert.Equal(expected, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.StandardOutput + result.StandardError));
    }

    [Fact]
    public async Task MissingOptionValue_ExitsThree()
    {
        using var directory = new TempArtifactDirectory("host-exit");
        var result = await RunHostAsync(directory.Path, "--backup");
        Assert.Equal(3, result.ExitCode);
        Assert.Contains("usage error", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnusableDataDirectory_ExitsTwo()
    {
        using var directory = new TempArtifactDirectory("host-exit");
        var filePath = directory.Combine("not-a-directory");
        await File.WriteAllTextAsync(filePath, "x");

        var result = await RunHostAsync(filePath, "--doctor");
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("startup failed", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<ProcessResult> RunHostAsync(string dataDirectory, params string[] arguments)
    {
        var hostDll = LocateHostAssembly();
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(hostDll)!
        };
        startInfo.ArgumentList.Add(hostDll);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment[EnvironmentNames.DataDirectory] = dataDirectory;
        startInfo.Environment[EnvironmentNames.McpEnabled] = "false";

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start AiAssistant.Host.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("AiAssistant.Host did not exit within 60 seconds.");
        }

        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    private static string LocateHostAssembly()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AiAssistant.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException("Repository root not found.");
        }

        var hostDll = Path.Combine(
            directory.FullName,
            ArtifactPaths.DefaultRootName,
            "bin",
            "AiAssistant.Host",
            "release",
            "AiAssistant.dll");
        Assert.True(File.Exists(hostDll), $"Host assembly not built: {hostDll}");
        return hostDll;
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
