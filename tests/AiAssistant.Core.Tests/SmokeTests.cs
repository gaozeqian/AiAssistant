using AiAssistant.Core;

namespace AiAssistant.Core.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void CoreAssembly_LoadsWithoutExternalDependencies()
    {
        var assembly = typeof(CoreAssemblyMarker).Assembly;
        Assert.NotNull(assembly);
    }
}
