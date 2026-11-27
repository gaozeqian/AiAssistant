using AiAssistant.Core.Configuration;
using AiAssistant.Testing.Common;

namespace AiAssistant.Host.Tests;

[Collection(ProcessEnvironmentCollectionName.Value)]
[Trait("Category", "Integration")]
public sealed class ServiceGraphTests
{
    [Fact]
    public async Task AppRuntime_ResolvesEveryCompositionRootService()
    {
        using var directory = new TempArtifactDirectory("host-di");
        using var scope = new ProcessEnvironmentScope(new Dictionary<string, string?>
        {
            [EnvironmentNames.DataDirectory] = directory.Path,
            [EnvironmentNames.McpEnabled] = "false"
        });

        await using var runtime = await AppRuntime.CreateAsync();

        Assert.NotNull(runtime.Services.Clock);
        Assert.NotNull(runtime.Services.McpControl);
        Assert.NotNull(runtime.Services.DatabaseMaintenance);
        Assert.NotNull(runtime.Services.DataDeletion);
        Assert.NotNull(runtime.Services.ModelBenchmark);
        Assert.NotNull(runtime.Services.ModelGateway);
        Assert.NotNull(runtime.Services.EmbeddingIndexer);
        Assert.NotNull(runtime.Services.ModelSecretAdmin);
        Assert.NotNull(runtime.Services.RangeReport);
        Assert.NotNull(runtime.Services.EvidenceSearch);
        Assert.NotNull(runtime.Services.AsrEngine);
        Assert.NotNull(runtime.Services.WakeWordDetector);
        Assert.NotNull(runtime.Services.ModelCapabilityProber);

        // The shell resolves the voice history/player and retention wiring, so building it proves
        // every new constructor dependency is actually registered.
        using var shell = runtime.CreateShellViewModel();
        Assert.NotNull(shell.Voice);
        Assert.NotNull(shell.Data);
    }
}
