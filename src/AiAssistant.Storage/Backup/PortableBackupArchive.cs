using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AiAssistant.Core.Exceptions;

namespace AiAssistant.Storage.Backup;

/// <summary>
/// Portable backup container: an ordinary ZIP with a plaintext manifest and one AES-256-GCM
/// encrypted payload. The payload is itself a ZIP holding the encrypted database, encrypted media
/// and the plaintext master key, so the archive can be restored on another machine where the
/// source machine's OS keystore is unavailable. The backup key never leaves the user.
/// </summary>
internal static class PortableBackupArchive
{
    internal const string ManifestEntryName = "manifest.json";
    internal const string PayloadEntryName = "payload.enc";
    internal const string FormatName = "AiAssistant.PortableBackup";
    internal const int FormatVersion = 1;

    private const int ChunkSize = 1024 * 1024;
    private const int TagSize = 16;
    private const int SaltSize = 16;
    private const int NoncePrefixSize = 8;
    private const int NonceSize = 12;
    private const int DerivedKeySize = 32;
    private const string MasterKeyEntryName = "master.key";

    private static readonly byte[] HkdfInfo = "AiAssistant.PortableBackup.v1"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static async Task WriteAsync(
        string destinationPath,
        string backupKey,
        string payloadZipPath,
        CancellationToken ct)
    {
        var key = ParseKey(backupKey);
        try
        {
            var salt = RandomNumberGenerator.GetBytes(SaltSize);
            var noncePrefix = RandomNumberGenerator.GetBytes(NoncePrefixSize);
            var manifest = new BackupManifest(
                FormatName,
                FormatVersion,
                "AES-256-GCM",
                "HKDF-SHA256",
                Convert.ToBase64String(salt),
                Convert.ToBase64String(noncePrefix),
                ChunkSize,
                new FileInfo(payloadZipPath).Length);
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
            var derivedKey = DeriveKey(key, salt);
            try
            {
                using var archive = ZipFile.Open(destinationPath, ZipArchiveMode.Create);
                var manifestEntry = archive.CreateEntry(ManifestEntryName, CompressionLevel.NoCompression);
                await using (var manifestStream = manifestEntry.Open())
                {
                    await manifestStream.WriteAsync(manifestBytes, ct);
                }

                var payloadEntry = archive.CreateEntry(PayloadEntryName, CompressionLevel.NoCompression);
                await using var payloadStream = payloadEntry.Open();
                await EncryptPayloadAsync(payloadZipPath, payloadStream, derivedKey, manifestBytes, noncePrefix, ct);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(derivedKey);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// Decrypts and extracts the payload into <paramref name="destinationDirectory"/>, then returns
    /// the plaintext master key. The caller owns the returned array and must zero it.
    /// </summary>
    internal static async Task<byte[]> ExtractAsync(
        string backupPath,
        string backupKey,
        string destinationDirectory,
        CancellationToken ct)
    {
        var key = ParseKey(backupKey);
        try
        {
            using var archive = ZipFile.OpenRead(backupPath);
            var manifestEntry = archive.GetEntry(ManifestEntryName)
                ?? throw new StorageIntegrityException("Backup archive is missing its manifest.");
            var payloadEntry = archive.GetEntry(PayloadEntryName)
                ?? throw new StorageIntegrityException("Backup archive is missing its encrypted payload.");

            byte[] manifestBytes;
            await using (var manifestStream = manifestEntry.Open())
            {
                using var memory = new MemoryStream();
                await manifestStream.CopyToAsync(memory, ct);
                manifestBytes = memory.ToArray();
            }

            var manifest = JsonSerializer.Deserialize<BackupManifest>(manifestBytes, JsonOptions)
                ?? throw new StorageIntegrityException("Backup manifest is empty.");
            ValidateManifest(manifest);
            var salt = DecodeManifestField(manifest.Salt, SaltSize, "salt");
            var noncePrefix = DecodeManifestField(manifest.NoncePrefix, NoncePrefixSize, "noncePrefix");
            var derivedKey = DeriveKey(key, salt);
            var payloadZipPath = Path.Combine(destinationDirectory, "payload.zip");
            try
            {
                await using (var payloadStream = payloadEntry.Open())
                {
                    await DecryptPayloadAsync(
                        payloadStream,
                        payloadZipPath,
                        derivedKey,
                        manifestBytes,
                        noncePrefix,
                        manifest.PayloadLength,
                        manifest.ChunkSize,
                        ct);
                }

                ZipFile.ExtractToDirectory(payloadZipPath, destinationDirectory, overwriteFiles: true);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(derivedKey);
                TryDeleteFile(payloadZipPath);
            }

            var masterKeyPath = Path.Combine(destinationDirectory, MasterKeyEntryName);
            if (!File.Exists(masterKeyPath))
            {
                throw new StorageIntegrityException("Backup payload does not contain master.key.");
            }

            var masterKey = await File.ReadAllBytesAsync(masterKeyPath, ct);
            TryDeleteFile(masterKeyPath);
            if (masterKey.Length != BackupKeyCodec.KeySizeBytes)
            {
                CryptographicOperations.ZeroMemory(masterKey);
                throw new StorageIntegrityException("Backup payload contains an invalid master key.");
            }

            return masterKey;
        }
        catch (CryptographicException ex)
        {
            throw new StorageAuthenticationException(
                "Backup key is incorrect or the encrypted payload is corrupted.",
                ex);
        }
        catch (JsonException ex)
        {
            throw new StorageIntegrityException("Backup manifest is not valid JSON.", ex);
        }
        catch (InvalidDataException ex)
        {
            throw new StorageIntegrityException("Backup archive is not a valid ZIP file.", ex);
        }
    }

    private static async Task EncryptPayloadAsync(
        string payloadZipPath,
        Stream output,
        byte[] derivedKey,
        byte[] manifestBytes,
        byte[] noncePrefix,
        CancellationToken ct)
    {
        var manifestHash = SHA256.HashData(manifestBytes);
        var buffer = new byte[ChunkSize];
        var ciphertext = new byte[ChunkSize];
        var tag = new byte[TagSize];
        var nonce = new byte[NonceSize];
        var associatedData = new byte[manifestHash.Length + sizeof(int)];
        using var aes = new AesGcm(derivedKey, TagSize);
        using var input = File.OpenRead(payloadZipPath);
        var chunkIndex = 0;
        while (true)
        {
            var read = await ReadChunkAsync(input, buffer, ct);
            if (read == 0)
            {
                break;
            }

            BuildNonce(nonce, noncePrefix, chunkIndex);
            BuildAssociatedData(associatedData, manifestHash, chunkIndex);
            var plaintext = buffer.AsSpan(0, read);
            var encrypted = ciphertext.AsSpan(0, read);
            aes.Encrypt(nonce, plaintext, encrypted, tag, associatedData);
            await output.WriteAsync(ciphertext.AsMemory(0, read), ct);
            await output.WriteAsync(tag.AsMemory(), ct);
            chunkIndex++;
        }

        if (chunkIndex == 0)
        {
            throw new StorageIntegrityException("Backup payload is empty.");
        }
    }

    private static async Task DecryptPayloadAsync(
        Stream payloadStream,
        string outputPath,
        byte[] derivedKey,
        byte[] manifestBytes,
        byte[] noncePrefix,
        long payloadLength,
        int chunkSize,
        CancellationToken ct)
    {
        var manifestHash = SHA256.HashData(manifestBytes);
        var buffer = new byte[chunkSize];
        var plaintext = new byte[chunkSize];
        var tag = new byte[TagSize];
        var nonce = new byte[NonceSize];
        var associatedData = new byte[manifestHash.Length + sizeof(int)];
        var trailing = new byte[1];
        using var aes = new AesGcm(derivedKey, TagSize);
        await using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        var remaining = payloadLength;
        var chunkIndex = 0;
        while (remaining > 0)
        {
            var chunkLength = (int)Math.Min(chunkSize, remaining);
            await ReadExactlyAsync(payloadStream, buffer.AsMemory(0, chunkLength), ct);
            await ReadExactlyAsync(payloadStream, tag.AsMemory(), ct);

            BuildNonce(nonce, noncePrefix, chunkIndex);
            BuildAssociatedData(associatedData, manifestHash, chunkIndex);
            var ciphertext = buffer.AsSpan(0, chunkLength);
            var decrypted = plaintext.AsSpan(0, chunkLength);
            aes.Decrypt(nonce, ciphertext, tag, decrypted, associatedData);
            await output.WriteAsync(plaintext.AsMemory(0, chunkLength), ct);
            remaining -= chunkLength;
            chunkIndex++;
        }

        // Any trailing bytes mean the archive was truncated, appended to or reordered.
        if (await payloadStream.ReadAsync(trailing, ct) != 0)
        {
            throw new StorageIntegrityException("Backup payload contains unexpected trailing data.");
        }
    }

    private static void BuildNonce(byte[] nonce, byte[] noncePrefix, int chunkIndex)
    {
        noncePrefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteInt32BigEndian(nonce.AsSpan(NoncePrefixSize), chunkIndex);
    }

    private static void BuildAssociatedData(byte[] associatedData, byte[] manifestHash, int chunkIndex)
    {
        manifestHash.CopyTo(associatedData, 0);
        BinaryPrimitives.WriteInt32BigEndian(associatedData.AsSpan(manifestHash.Length), chunkIndex);
    }

    private static async Task<int> ReadChunkAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], ct);
            if (read == 0)
            {
                throw new StorageIntegrityException("Backup payload ended unexpectedly.");
            }

            total += read;
        }
    }

    private static byte[] DeriveKey(byte[] key, byte[] salt) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, key, DerivedKeySize, salt, HkdfInfo);

    private static byte[] DecodeManifestField(string value, int expectedLength, string fieldName)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(value);
        }
        catch (FormatException ex)
        {
            throw new StorageIntegrityException(
                $"Backup manifest field '{fieldName}' is not valid base64.",
                ex);
        }

        if (bytes.Length != expectedLength)
        {
            throw new StorageIntegrityException(
                $"Backup manifest field '{fieldName}' has an unexpected length.");
        }

        return bytes;
    }

    private static void ValidateManifest(BackupManifest manifest)
    {
        if (!string.Equals(manifest.Format, FormatName, StringComparison.Ordinal) ||
            manifest.Version != FormatVersion ||
            !string.Equals(manifest.Cipher, "AES-256-GCM", StringComparison.Ordinal) ||
            !string.Equals(manifest.Kdf, "HKDF-SHA256", StringComparison.Ordinal))
        {
            throw new StorageIntegrityException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Unsupported backup format '{manifest.Format}' version {manifest.Version}."));
        }

        if (manifest.ChunkSize is <= 0 or > 16 * 1024 * 1024 || manifest.PayloadLength <= 0)
        {
            throw new StorageIntegrityException("Backup manifest contains invalid length parameters.");
        }
    }

    private static byte[] ParseKey(string backupKey)
    {
        try
        {
            return BackupKeyCodec.Parse(backupKey);
        }
        catch (ArgumentException ex)
        {
            throw new StorageAuthenticationException("Backup key format is invalid.", ex);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // The caller deletes the whole temporary directory; a lingering file must not mask the
            // real restore error.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    internal sealed record BackupManifest(
        string Format,
        int Version,
        string Cipher,
        string Kdf,
        string Salt,
        string NoncePrefix,
        int ChunkSize,
        long PayloadLength);
}
