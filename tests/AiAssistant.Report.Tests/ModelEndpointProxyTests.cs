using AiAssistant.Core.Domain;
using AiAssistant.Models;

namespace AiAssistant.Report.Tests;

/// <summary>Local model endpoints must bypass inherited system proxy settings (corporate proxy env vars).</summary>
public sealed class ModelEndpointProxyTests
{
    [Fact]
    public void LoopbackEndpoint_DoesNotUseSystemProxy()
    {
        Assert.False(ModelHttpClientFactory.ShouldUseProxy(Create(new Uri("http://127.0.0.1:11434"))));
        Assert.False(ModelHttpClientFactory.ShouldUseProxy(Create(new Uri("http://localhost:1234"))));
        Assert.False(ModelHttpClientFactory.ShouldUseProxy(Create(new Uri("http://[::1]:11434"))));
    }

    [Fact]
    public void RemoteEndpoint_UsesSystemProxyByDefault()
    {
        Assert.True(ModelHttpClientFactory.ShouldUseProxy(Create(new Uri("https://models.example.com"))));
    }

    [Fact]
    public void ExplicitProxy_OverridesLoopbackBypass()
    {
        var endpoint = Create(new Uri("http://127.0.0.1:11434")) with { Proxy = new Uri("http://proxy.internal:8080") };
        Assert.True(ModelHttpClientFactory.ShouldUseProxy(endpoint));
    }

    private static ModelEndpointSettings Create(Uri baseAddress) =>
        new(
            ModelEndpointTier.LocalOffline,
            ModelProviderKind.Ollama,
            baseAddress,
            "qwen3:8b",
            "qwen3-embedding:0.6b",
            TimeSpan.FromSeconds(30),
            null,
            null,
            null,
            null,
            null,
            null,
            AllowRemoteEndpoint: false);
}
