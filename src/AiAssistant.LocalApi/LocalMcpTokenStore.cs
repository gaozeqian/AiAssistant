using System.Security.Cryptography;
using System.Text;
using AiAssistant.Application.Ports;

namespace AiAssistant.LocalApi;

/// <summary>
/// AES-GCM encrypted bearer token for the local MCP endpoint. The wrapping key lives in the OS key store,
/// so a stolen data directory alone cannot recover the token. Users copy the token from the diagnostics panel.
/// </summary>
public sealed class LocalMcpTokenStore : IDisposable
{
    private const string KeyId = "local-mcp-token-v1";
    private static readonly byte[] Magic = "AIMT"u8.ToArray();
    private static readonly byte[] Aad = "AiAssistant.LocalApi.McpToken.v1"u8.ToArray();
    private readonly string _filePath;
    private readonly IKeyStore _keyStore;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public LocalMcpTokenStore(string directory, IKeyStore keyStore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(keyStore);
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "local-mcp-token.enc");
        _keyStore = keyStore;
    }

    public string FilePath => _filePath;

    public async Task<string> GetOrCreateAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(ct);
        try
        {
            var existing = await ReadUnlockedAsync(ct);
            if (!string.IsNullOrWhiteSpace(existing))
            {
                return existing;
            }

            return await WriteNewTokenUnlockedAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Invalidates the previous token immediately; running servers must be restarted to pick it up.</summary>
    public async Task<string> RotateAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(ct);
        try
        {
            return await WriteNewTokenUnlockedAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RevokeAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(ct);
        try
        {
            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }

    private async Task<string> WriteNewTokenUnlockedAsync(CancellationToken ct)
    {
        var token = GenerateToken();
        var payload = await EncryptAsync(token, ct);
        await File.WriteAllBytesAsync(_filePath, payload, ct);
        RestrictPermissions(_filePath);
        return token;
    }

    private async Task<string?> ReadUnlockedAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
        {
            return null;
        }

        var payload = await File.ReadAllBytesAsync(_filePath, ct);
        if (payload.Length < 33 || !payload.AsSpan(0, 4).SequenceEqual(Magic))
        {
            return null;
        }

        var key = await _keyStore.GetOrCreateMasterKeyAsync(KeyId, ct);
        byte[]? plaintext = null;
        try
        {
            plaintext = new byte[payload.Length - 32];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(payload.AsSpan(4, 12), payload.AsSpan(32), payload.AsSpan(16, 16), plaintext, Aad);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException)
        {
            return null;
        }
        finally
        {
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }

            CryptographicOperations.ZeroMemory(key);
        }
    }

    private async Task<byte[]> EncryptAsync(string token, CancellationToken ct)
    {
        var key = await _keyStore.GetOrCreateMasterKeyAsync(KeyId, ct);
        var plaintext = Encoding.UTF8.GetBytes(token);
        var cipher = new byte[plaintext.Length];
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        try
        {
            using (var aes = new AesGcm(key, 16))
            {
                aes.Encrypt(nonce, plaintext, cipher, tag, Aad);
            }

            var payload = new byte[4 + 12 + 16 + cipher.Length];
            Magic.CopyTo(payload, 0);
            nonce.CopyTo(payload, 4);
            tag.CopyTo(payload, 16);
            cipher.CopyTo(payload, 32);
            return payload;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static string GenerateToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static void RestrictPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
