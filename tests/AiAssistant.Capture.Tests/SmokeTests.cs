using AiAssistant.Capture;

namespace AiAssistant.Capture.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void CaptureAssembly_Loads()
    {
        Assert.NotNull(typeof(CaptureAssemblyMarker).Assembly);
    }
}
