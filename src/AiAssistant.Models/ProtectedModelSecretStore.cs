using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiAssistant.Application.Ports;

namespace AiAssistant.Models;

/// <summary>
/// Whole-file AES-GCM encrypted secret store for endpoint API keys and client certificate passwords.
/// The wrapping key comes from the OS key store; models.json only contains secret references.
/// </summary>
public sealed class ProtectedModelSecretStore : IDisposable
{
    private static readonly byte[] Magic = "AIM2"u8.ToArray();
    private static readonly byte[] Aad = "AiAssistant.Models.EndpointSecrets.v1"u8.ToArray();
    private readonly string _filePath;
    private readonly IKeyStore _keyStore;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ProtectedModelSecretStore(string directory, IKeyStore keyStore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(keyStore);
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "model-secrets.enc");
        _keyStore = keyStore;
    }

    public async Task<string?> GetAsync(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var all = await LoadAllAsync(ct);
        return all.TryGetValue(name, out var value) ? value : null;
    }

    public async Task SetAsync(string name, string value, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        await _gate.WaitAsync(ct);
        try
        {
            var all = await LoadAllUnlockedAsync(ct);
            all[name] = value;
            await SaveAllUnlockedAsync(all, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await _gate.WaitAsync(ct);
        try
        {
            var all = await LoadAllUnlockedAsync(ct);
            if (!all.Remove(name))
            {
                return false;
            }

            await SaveAllUnlockedAsync(all, ct);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Lists secret names only; values are never returned.</summary>
    public async Task<IReadOnlyList<string>> ListNamesAsync(CancellationToken ct)
    {
        var all = await LoadAllAsync(ct);
        return all.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray();
    }

    private async Task<Dictionary<string, string>> LoadAllAsync(CancellationToken ct)    {
        await _gate.WaitAsync(ct);
        try
        {
            return await LoadAllUnlockedAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, string>> LoadAllUnlockedAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var payload = await File.ReadAllBytesAsync(_filePath, ct);
        if (payload.Length < 33 || !payload.AsSpan(0, 4).SequenceEqual(Magic))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var key = await _keyStore.GetOrCreateMasterKeyAsync("model-endpoint-secrets-v1", ct);
        byte[]? plaintext = null;
        try
        {
            plaintext = new byte[payload.Length - 32];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(payload.AsSpan(4, 12), payload.AsSpan(32), payload.AsSpan(16, 16), plaintext, Aad);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(plaintext)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (CryptographicException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
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

    private async Task SaveAllUnlockedAsync(Dictionary<string, string> values, CancellationToken ct)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(values);
        var key = await _keyStore.GetOrCreateMasterKeyAsync("model-endpoint-secrets-v1", ct);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];
            using (var aes = new AesGcm(key, 16))
            {
                aes.Encrypt(nonce, plaintext, ciphertext, tag, Aad);
            }

            var output = new byte[32 + ciphertext.Length];
            Magic.CopyTo(output, 0);
            nonce.CopyTo(output, 4);
            tag.CopyTo(output, 16);
            ciphertext.CopyTo(output, 32);
            var temp = $"{_filePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllBytesAsync(temp, output, ct);
                File.Move(temp, _filePath, overwrite: true);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(_filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
            finally
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }

            CryptographicOperations.ZeroMemory(output);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(key);
        }
    }
    public void Dispose() => _gate.Dispose();
}

public sealed class ModelSecretResolver(ProtectedModelSecretStore store) : IModelSecretResolver
{
    public async Task<string?> ResolveAsync(string reference, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        if (reference.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            return Environment.GetEnvironmentVariable(reference[4..]);
        }

        return await store.GetAsync(reference, ct);
    }
}

/// <summary>Application-facing administration of endpoint secrets; never exposes stored values.</summary>
public sealed class ModelSecretAdmin(ProtectedModelSecretStore store) : AiAssistant.Application.Ports.IModelSecretAdmin
{
    public Task<IReadOnlyList<string>> ListNamesAsync(CancellationToken ct) => store.ListNamesAsync(ct);

    public Task SetAsync(string name, string value, CancellationToken ct) => store.SetAsync(name, value, ct);

    public Task<bool> DeleteAsync(string name, CancellationToken ct) => store.DeleteAsync(name, ct);
}
