using AiAssistant.Platform.Windows;

namespace AiAssistant.Platform.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void PlatformAssemblies_Load()
    {
        Assert.NotNull(typeof(PlatformWindowsAssemblyMarker).Assembly);
    }
}
