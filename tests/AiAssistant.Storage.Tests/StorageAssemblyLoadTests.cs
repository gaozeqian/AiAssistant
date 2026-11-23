using AiAssistant.Storage;

namespace AiAssistant.Storage.Tests;

public sealed class StorageAssemblyLoadTests
{
    [Fact]
    public void StorageAssembly_Loads()
    {
        Assert.NotNull(typeof(StorageAssemblyMarker).Assembly);
    }
}
