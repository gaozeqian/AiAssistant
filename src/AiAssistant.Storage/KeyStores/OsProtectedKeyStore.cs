using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Exceptions;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Storage.KeyStores;

/// <summary>
/// Cross-platform master key store using envelope encryption:
/// - Windows: DPAPI CurrentUser protection (ProtectedData)
/// - macOS: Keychain (/usr/bin/security) with user-0600 KEK fallback in headless environments
/// - Linux: libsecret (secret-tool) with user-0600 KEK fallback in headless environments
/// Supports wrapper-key rotation without rewriting encrypted media blobs (ADR-0003).
/// </summary>
public sealed class OsProtectedKeyStore : IKeyStore, IDisposable
{
    private static readonly byte[] EntropyBase = "AiAssistant.KeyStore.Envelope.v1"u8.ToArray();
    private readonly string _keyDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _simulateRevokedPermission;

    public OsProtectedKeyStore(StorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _keyDirectory = options.KeyStoreDirectory;
        Capability = DetectCapability();
    }

    public CapabilityState Capability { get; private set; }

    public void Dispose() => _gate.Dispose();

    public void SimulatePermissionRevoked(bool revoked)
    {
        _simulateRevokedPermission = revoked;
        Capability = revoked
            ? CapabilityState.PermissionRequired(CapabilityKind.KeyStore, "OS keystore access revoked", "Re-authenticate user session or unlock system keyring.")
            : DetectCapability();
    }

    public IKeyStore CreateForDirectory(string dataDirectory) =>
        new OsProtectedKeyStore(new StorageOptions { DataDirectory = dataDirectory });

    public async Task<byte[]> GetOrCreateMasterKeyAsync(string keyId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        ct.ThrowIfCancellationRequested();

        if (_simulateRevokedPermission)
        {
            throw new PlatformPermissionException("Access to the OS keystore has been revoked or locked.");
        }

        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(_keyDirectory);
            var dekFile = GetDekEnvelopePath(keyId);
            var generationFile = GetGenerationPath(keyId);

            if (File.Exists(dekFile))
            {
                var generation = File.Exists(generationFile)
                    ? await File.ReadAllTextAsync(generationFile, Encoding.UTF8, ct)
                    : "gen-1";
                var wrapped = await File.ReadAllBytesAsync(dekFile, ct);
                return UnwrapDek(keyId, generation.Trim(), wrapped);
            }

            var dek = RandomNumberGenerator.GetBytes(32);
            const string initialGeneration = "gen-1";
            var newWrapped = WrapDek(keyId, initialGeneration, dek);

            await WriteFileAtomicAsync(generationFile, Encoding.UTF8.GetBytes(initialGeneration), ct);
            await WriteFileAtomicAsync(dekFile, newWrapped, ct);
            return dek;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RotateWrapperKeyAsync(string keyId, CancellationToken ct)
    {
        var dek = await GetOrCreateMasterKeyAsync(keyId, ct);
        await _gate.WaitAsync(ct);
        try
        {
            var newGeneration = $"gen-{Guid.NewGuid():N}";
            var rewrapped = WrapDek(keyId, newGeneration, dek);
            var dekFile = GetDekEnvelopePath(keyId);
            var generationFile = GetGenerationPath(keyId);

            await WriteFileAtomicAsync(dekFile, rewrapped, ct);
            await WriteFileAtomicAsync(generationFile, Encoding.UTF8.GetBytes(newGeneration), ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
            _gate.Release();
        }
    }

    private byte[] WrapDek(string keyId, string generation, byte[] dek)
    {
        var aad = Encoding.UTF8.GetBytes($"{keyId}:{generation}");
        if (OperatingSystem.IsWindows())
        {
            return ProtectWindowsDpapi(dek, aad);
        }

        var kek = GetOrCreatePosixKek(keyId, generation);
        try
        {
            return AesGcmWrap(kek, dek, aad);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    private byte[] UnwrapDek(string keyId, string generation, byte[] wrapped)
    {
        var aad = Encoding.UTF8.GetBytes($"{keyId}:{generation}");
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return UnprotectWindowsDpapi(wrapped, aad);
            }

            var kek = GetOrCreatePosixKek(keyId, generation);
            try
            {
                return AesGcmUnwrap(kek, wrapped, aad);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(kek);
            }
        }
        catch (CryptographicException ex)
        {
            throw new StorageAuthenticationException(
                $"Failed to unwrap master key '{keyId}' from OS keystore. Key envelope is corrupted or belongs to another OS user.",
                ex);
        }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ProtectWindowsDpapi(byte[] plaintext, byte[] aad)
    {
        var entropy = CombineEntropy(aad);
        return ProtectedData.Protect(plaintext, entropy, DataProtectionScope.CurrentUser);
    }

    [SupportedOSPlatform("windows")]
    private static byte[] UnprotectWindowsDpapi(byte[] protectedBytes, byte[] aad)
    {
        var entropy = CombineEntropy(aad);
        return ProtectedData.Unprotect(protectedBytes, entropy, DataProtectionScope.CurrentUser);
    }

    private byte[] GetOrCreatePosixKek(string keyId, string generation)
    {
        var accountName = $"{keyId}.{generation}";
        if (OperatingSystem.IsMacOS() && TryReadOrWriteMacKeychain(accountName, out var macKek))
        {
            return macKek;
        }

        if (OperatingSystem.IsLinux() && TryReadOrWriteLinuxSecretTool(accountName, out var linuxKek))
        {
            return linuxKek;
        }

        // User-restricted (0600) local KEK file fallback when headless session lacks D-Bus keyring or unlocked Keychain
        var kekPath = Path.Combine(_keyDirectory, $"{SanitizeFileName(accountName)}.kek");
        if (File.Exists(kekPath))
        {
            return File.ReadAllBytes(kekPath);
        }

        var kek = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(kekPath, kek);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(kekPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return kek;
    }

    private static bool TryReadOrWriteMacKeychain(string account, out byte[] kek)
    {
        kek = [];
        try
        {
            var find = RunProcess("/usr/bin/security", ["find-generic-password", "-s", "AiAssistant", "-a", account, "-w"]);
            if (find.ExitCode == 0 && !string.IsNullOrWhiteSpace(find.StdOut))
            {
                kek = Convert.FromHexString(find.StdOut.Trim());
                return kek.Length == 32;
            }

            var generated = RandomNumberGenerator.GetBytes(32);
            var hex = Convert.ToHexStringLower(generated);
            var add = RunProcess("/usr/bin/security", ["add-generic-password", "-U", "-s", "AiAssistant", "-a", account, "-w", hex]);
            if (add.ExitCode == 0)
            {
                kek = generated;
                return true;
            }
        }
        catch
        {
            // Fall back to 0600 user KEK envelope if security binary / unlocked keychain unavailable
        }

        return false;
    }

    private static bool TryReadOrWriteLinuxSecretTool(string account, out byte[] kek)
    {
        kek = [];
        try
        {
            var lookup = RunProcess("secret-tool", ["lookup", "service", "AiAssistant", "account", account]);
            if (lookup.ExitCode == 0 && !string.IsNullOrWhiteSpace(lookup.StdOut))
            {
                kek = Convert.FromHexString(lookup.StdOut.Trim());
                return kek.Length == 32;
            }

            var generated = RandomNumberGenerator.GetBytes(32);
            var hex = Convert.ToHexStringLower(generated);
            var store = RunProcess("secret-tool", ["store", "--label=AiAssistant", "service", "AiAssistant", "account", account], hex);
            if (store.ExitCode == 0)
            {
                kek = generated;
                return true;
            }
        }
        catch
        {
            // Fall back to 0600 user KEK envelope when secret-tool or D-Bus session is unavailable
        }

        return false;
    }

    private static (int ExitCode, string StdOut) RunProcess(string fileName, IReadOnlyList<string> args, string? stdIn = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdIn is not null,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi);
        if (process is null)
        {
            return (-1, string.Empty);
        }

        if (stdIn is not null)
        {
            process.StandardInput.Write(stdIn);
            process.StandardInput.Close();
        }

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(3000);
        return (process.ExitCode, output);
    }

    private static byte[] AesGcmWrap(byte[] kek, byte[] plaintext, byte[] aad)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ciphertext = new byte[plaintext.Length];
        using var aes = new AesGcm(kek, 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);

        var combined = new byte[12 + 16 + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, combined, 0, 12);
        Buffer.BlockCopy(tag, 0, combined, 12, 16);
        Buffer.BlockCopy(ciphertext, 0, combined, 28, ciphertext.Length);
        return combined;
    }

    private static byte[] AesGcmUnwrap(byte[] kek, byte[] combined, byte[] aad)
    {
        if (combined.Length < 28)
        {
            throw new CryptographicException("Invalid wrapped key envelope length.");
        }

        var nonce = combined.AsSpan(0, 12);
        var tag = combined.AsSpan(12, 16);
        var ciphertext = combined.AsSpan(28);
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(kek, 16);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
        return plaintext;
    }

    private static byte[] CombineEntropy(byte[] aad)
    {
        var combined = new byte[EntropyBase.Length + aad.Length];
        Buffer.BlockCopy(EntropyBase, 0, combined, 0, EntropyBase.Length);
        Buffer.BlockCopy(aad, 0, combined, EntropyBase.Length, aad.Length);
        return combined;
    }

    private static CapabilityState DetectCapability()
    {
        if (OperatingSystem.IsWindows())
        {
            return CapabilityState.Supported(CapabilityKind.KeyStore, "Windows DPAPI (CurrentUser)");
        }

        if (OperatingSystem.IsMacOS())
        {
            return File.Exists("/usr/bin/security")
                ? CapabilityState.Supported(CapabilityKind.KeyStore, "macOS Keychain")
                : CapabilityState.Degraded(CapabilityKind.KeyStore, "macOS user-0600 envelope fallback");
        }

        if (OperatingSystem.IsLinux())
        {
            return CapabilityState.Supported(CapabilityKind.KeyStore, "Linux libsecret / user-0600 envelope");
        }

        return CapabilityState.Degraded(CapabilityKind.KeyStore, "User-scoped file envelope");
    }

    private string GetDekEnvelopePath(string keyId) =>
        Path.Combine(_keyDirectory, $"{SanitizeFileName(keyId)}.dek.enc");

    private string GetGenerationPath(string keyId) =>
        Path.Combine(_keyDirectory, $"{SanitizeFileName(keyId)}.gen");

    private static string SanitizeFileName(string value) =>
        string.Concat(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_'));

    private static async Task WriteFileAtomicAsync(string targetPath, byte[] bytes, CancellationToken ct)
    {
        var tempPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllBytesAsync(tempPath, bytes, ct);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(tempPath, targetPath, overwrite: true);
    }
}

/// <summary>
/// Deterministic key store for isolated tests (e.g., wrong-key tests without touching developer keystore).
/// </summary>
public sealed class StaticKeyStore(byte[] masterKey) : IKeyStore
{
    private byte[] _key = masterKey.ToArray();

    public CapabilityState Capability => CapabilityState.Supported(CapabilityKind.KeyStore, "StaticTestKeyStore");

    public Task<byte[]> GetOrCreateMasterKeyAsync(string keyId, CancellationToken ct) =>
        Task.FromResult(_key.ToArray());

    public Task RotateWrapperKeyAsync(string keyId, CancellationToken ct) =>
        Task.CompletedTask;

    public IKeyStore CreateForDirectory(string dataDirectory) => this;

    public void OverrideKeyForTesting(byte[] newKey) => _key = newKey.ToArray();
}
