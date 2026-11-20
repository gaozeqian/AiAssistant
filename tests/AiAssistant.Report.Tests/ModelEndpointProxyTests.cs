using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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

    [Fact]
    public void EnterpriseCaPemWithoutPrivateKey_Loads()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "test-temp");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"aiassistant-ca-{Guid.NewGuid():N}.crt");
        try
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=AiAssistant Test CA",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            using var certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(1));
            File.WriteAllText(path, certificate.ExportCertificatePem());

            var endpoint = Create(new Uri("https://127.0.0.1:47901")) with
            {
                Tier = ModelEndpointTier.CorporateIntranet,
                EnterpriseCaPath = path,
                AllowRemoteEndpoint = true
            };

            using var client = ModelHttpClientFactory.Create(endpoint);
            Assert.NotNull(client);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static ModelEndpointSettings Create(Uri baseAddress) =>
        new(
            ModelEndpointTier.LocalOffline,
            ModelProviderKind.Ollama,
            baseAddress,
            "gemma4:12b",
            "embeddinggemma-2:740m",
            TimeSpan.FromSeconds(30),
            null,
            null,
            null,
            null,
            null,
            null,
            AllowRemoteEndpoint: false);
}
