using AiAssistant.Core;

namespace AiAssistant.Core.Tests;

public sealed class CoreAssemblyLoadTests
{
    [Fact]
    public void CoreAssembly_LoadsWithoutExternalDependencies()
    {
        var assembly = typeof(CoreAssemblyMarker).Assembly;
        Assert.NotNull(assembly);
    }
}
