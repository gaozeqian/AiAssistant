using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace AiAssistant.Models;

/// <summary>
/// Builds a dedicated HttpClient per endpoint with proxy, client certificate (mTLS) and enterprise CA trust.
/// Credentials are held only in memory and never written to logs. Certificate handles are owned by the
/// SocketsHttpHandler so they are released exactly when the HttpClient (and therefore the handler) is disposed.
/// </summary>
public static class ModelHttpClientFactory
{
    public static HttpClient Create(ModelEndpointSettings endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var clientCertificate = string.IsNullOrWhiteSpace(endpoint.ClientCertificatePath)
            ? null
            : LoadClientCertificate(endpoint);
        var enterpriseCa = string.IsNullOrWhiteSpace(endpoint.EnterpriseCaPath)
            ? null
            : X509CertificateLoader.LoadCertificateFromFile(endpoint.EnterpriseCaPath);

        var socketsHandler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(Math.Min(30, endpoint.Timeout.TotalSeconds)),
            UseProxy = ShouldUseProxy(endpoint)
        };

        if (endpoint.Proxy is not null)
        {
            socketsHandler.Proxy = new WebProxy(endpoint.Proxy);
        }

        var ssl = new SslClientAuthenticationOptions();
        if (clientCertificate is not null)
        {
            ssl.ClientCertificates = new X509CertificateCollection { clientCertificate };
        }

        if (enterpriseCa is not null)
        {
            ssl.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                ValidateWithEnterpriseCa(certificate, errors, enterpriseCa);
        }

        socketsHandler.SslOptions = ssl;
        // SocketsHttpHandler is sealed on net10.0; a delegating handler owns the certificate handles
        // and releases them (together with the inner handler) when HttpClient is disposed.
        var handler = new CertificateOwnerHandler(socketsHandler, clientCertificate, enterpriseCa);
        var client = new HttpClient(handler)
        {
            BaseAddress = endpoint.BaseAddress,
            Timeout = endpoint.Timeout
        };
        if (!string.IsNullOrWhiteSpace(endpoint.ApiKey))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
        }

        return client;
    }

    /// <summary>
    /// Loopback model endpoints must never inherit system proxy settings (common with corporate proxy env vars),
    /// otherwise local Ollama/LM Studio calls fail. An explicitly configured proxy still wins.
    /// </summary>
    public static bool ShouldUseProxy(ModelEndpointSettings endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return endpoint.Proxy is not null || !endpoint.BaseAddress.IsLoopback;
    }

    /// <summary>
    /// Owns certificate handles for the lifetime of the handler; no closure keeps them alive after disposal.
    /// </summary>
    private sealed class CertificateOwnerHandler(
        HttpMessageHandler innerHandler,
        X509Certificate2? clientCertificate,
        X509Certificate2? enterpriseCa) : DelegatingHandler(innerHandler)
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                clientCertificate?.Dispose();
                enterpriseCa?.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private static X509Certificate2 LoadClientCertificate(ModelEndpointSettings endpoint)
    {
        var path = endpoint.ClientCertificatePath!;
        var extension = Path.GetExtension(path);
        if (string.Equals(extension, ".pem", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".crt", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(endpoint.ClientCertificateKeyPath)
                ? X509Certificate2.CreateFromPemFile(path)
                : X509Certificate2.CreateFromPemFile(path, endpoint.ClientCertificateKeyPath);
        }

        return string.IsNullOrWhiteSpace(endpoint.ClientCertificatePassword)
            ? X509CertificateLoader.LoadPkcs12FromFile(path, null)
            : X509CertificateLoader.LoadPkcs12FromFile(path, endpoint.ClientCertificatePassword);
    }

    private static bool ValidateWithEnterpriseCa(
        X509Certificate? certificate,
        SslPolicyErrors errors,
        X509Certificate2 ca)
    {
        if (certificate is null || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
        {
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        using var leaf = new X509Certificate2(certificate);
        return chain.Build(leaf);
    }
}
