using AiAssistant.Capture;

namespace AiAssistant.Capture.Tests;

public sealed class CaptureAssemblyLoadTests
{
    [Fact]
    public void CaptureAssembly_Loads()
    {
        Assert.NotNull(typeof(CaptureAssemblyMarker).Assembly);
    }
}
