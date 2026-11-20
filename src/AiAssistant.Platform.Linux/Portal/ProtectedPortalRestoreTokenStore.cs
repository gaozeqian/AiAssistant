using System.Security.Cryptography;
using System.Text;
using AiAssistant.Application.Ports;

namespace AiAssistant.Platform.Linux.Portal;

/// <summary>
/// Encrypts the single-use xdg portal restore token with a key held by the OS key store.
/// Token material never appears in plaintext files or diagnostic logs.
/// </summary>
public sealed class ProtectedPortalRestoreTokenStore(string directory, IKeyStore keyStore) : IPortalRestoreTokenStore
{
    private static readonly byte[] Magic = "AIP1"u8.ToArray();
    private static readonly byte[] Aad = "AiAssistant.XdgPortalRestoreToken.v1"u8.ToArray();
    private readonly string _filePath = Path.Combine(directory, "screencast-token.enc");

    public async Task<string?> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
        {
            return null;
        }

        var file = await File.ReadAllBytesAsync(_filePath, ct);
        if (file.Length < 32 || file.Length > 4096 || !file.AsSpan(0, 4).SequenceEqual(Magic))
        {
            return null;
        }

        var key = await keyStore.GetOrCreateMasterKeyAsync("xdg-portal-restore-token-v1", ct);
        byte[]? plaintext = null;
        try
        {
            plaintext = new byte[file.Length - 32];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(file.AsSpan(4, 12), file.AsSpan(32), file.AsSpan(16, 16), plaintext, Aad);
            var token = Encoding.UTF8.GetString(plaintext);
            return string.IsNullOrWhiteSpace(token) ? null : token;
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

    public async Task SaveAsync(string token, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        Directory.CreateDirectory(directory);
        var plaintext = Encoding.UTF8.GetBytes(token);
        var key = await keyStore.GetOrCreateMasterKeyAsync("xdg-portal-restore-token-v1", ct);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];
            using (var aes = new AesGcm(key, 16))
            {
                aes.Encrypt(nonce, plaintext, ciphertext, tag, Aad);
            }

            var output = new byte[32 + plaintext.Length];
            Magic.CopyTo(output, 0);
            nonce.CopyTo(output, 4);
            tag.CopyTo(output, 16);
            ciphertext.CopyTo(output, 32);
            await File.WriteAllBytesAsync(_filePath, output, ct);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(_filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            CryptographicOperations.ZeroMemory(output);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public Task ClearAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (File.Exists(_filePath))
        {
            File.Delete(_filePath);
        }

        return Task.CompletedTask;
    }
}
