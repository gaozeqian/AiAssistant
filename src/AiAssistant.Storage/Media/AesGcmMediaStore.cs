using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Exceptions;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace AiAssistant.Storage.Media;

/// <summary>
/// Stores media blobs encrypted at rest with AES-256-GCM using HKDF-derived per-content keys
/// and tracks reference counts &amp; expiration in the encrypted SQLite database (ADR-0003).
/// </summary>
public sealed class AesGcmMediaStore : IMediaStore
{
    private static readonly byte[] MagicHeader = "AIM1"u8.ToArray();
    private static readonly byte[] HkdfInfo = "AiAssistant.Media.AES256GCM.v1"u8.ToArray();

    private readonly EncryptedSqliteConnectionFactory _connectionFactory;
    private readonly IKeyStore _keyStore;

    public AesGcmMediaStore(EncryptedSqliteConnectionFactory connectionFactory, IKeyStore keyStore)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(keyStore);
        _connectionFactory = connectionFactory;
        _keyStore = keyStore;
    }

    internal sealed record StagedEncryptedBlob(
        ContentRef Reference,
        string RelativePath,
        string FullPath);

    public async Task<ContentRef> PutAsync(ContentWriteRequest request, CancellationToken ct)
    {
        var staged = await StageEncryptedBlobAsync(request, ct);
        try
        {
            await using var conn = await _connectionFactory.OpenConnectionAsync(ct);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
            await UpsertContentRefAsync(conn, tx, staged, ct);
            await tx.CommitAsync(ct);
            return staged.Reference;
        }
        catch (Exception ex)
        {
            DeletePhysicalBlobQuietly(staged.FullPath);
            throw EncryptedSqliteConnectionFactory.TranslateSqliteException(ex, staged.FullPath);
        }
    }

    public async Task<byte[]?> ReadDecryptedAsync(string contentId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentId);

        string relativePath;
        ContentKind kind;
        await using (var conn = await _connectionFactory.OpenConnectionAsync(ct))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT relative_path, kind
                FROM content_refs
                WHERE content_id = $id AND ref_count > 0;
                """;
            cmd.Parameters.AddWithValue("$id", contentId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }

            relativePath = reader.GetString(0);
            kind = (ContentKind)reader.GetInt32(1);
        }

        var fullPath = Path.Combine(_connectionFactory.Options.MediaDirectory, relativePath);
        if (!File.Exists(fullPath))
        {
            throw new StorageIntegrityException($"Referenced encrypted media file '{relativePath}' is missing on disk.");
        }

        var payload = await File.ReadAllBytesAsync(fullPath, ct);
        if (payload.Length < 4 + 12 + 16 || !payload.AsSpan(0, 4).SequenceEqual(MagicHeader))
        {
            throw new StorageIntegrityException($"Encrypted media file '{relativePath}' has an invalid or corrupted header.");
        }

        var derivedKey = await DeriveContentKeyAsync(contentId, ct);
        try
        {
            var nonce = payload.AsSpan(4, 12);
            var tag = payload.AsSpan(16, 16);
            var ciphertext = payload.AsSpan(32);
            var plaintext = new byte[ciphertext.Length];
            var aad = BuildAad(contentId, kind);

            using var aes = new AesGcm(derivedKey, 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
            return plaintext;
        }
        catch (CryptographicException ex)
        {
            throw new StorageIntegrityException(
                $"AES-GCM authentication failed for media '{contentId}'. File was tampered with or key does not match.",
                ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedKey);
        }
    }

    public async Task<int> IncrementRefAsync(string contentId, CancellationToken ct)
    {
        await using var conn = await _connectionFactory.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE content_refs
            SET ref_count = ref_count + 1
            WHERE content_id = $id
            RETURNING ref_count;
            """;
        cmd.Parameters.AddWithValue("$id", contentId);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null ? 0 : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    public async Task<int> DecrementRefAsync(string contentId, CancellationToken ct)
    {
        await using var conn = await _connectionFactory.OpenConnectionAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        var remaining = await DecrementRefInTransactionAsync(conn, tx, contentId, ct);
        await tx.CommitAsync(ct);
        return remaining.RemainingCount;
    }

    public async Task<int> PurgeExpiredAsync(DateTimeOffset nowUtc, CancellationToken ct)
    {
        var expired = new List<(string ContentId, string RelativePath)>();
        await using var conn = await _connectionFactory.OpenConnectionAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        await using (var selectCmd = conn.CreateCommand())
        {
            selectCmd.Transaction = tx;
            selectCmd.CommandText = """
                SELECT content_id, relative_path
                FROM content_refs
                WHERE (expires_at_utc IS NOT NULL AND expires_at_utc <= $nowUtc)
                   OR ref_count <= 0;
                """;
            selectCmd.Parameters.AddWithValue("$nowUtc", nowUtc.ToUniversalTime().ToString("O"));
            await using var reader = await selectCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                expired.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        foreach (var item in expired)
        {
            await using (var unlinkCmd = conn.CreateCommand())
            {
                unlinkCmd.Transaction = tx;
                unlinkCmd.CommandText = "UPDATE events SET content_id = NULL WHERE content_id = $id;";
                unlinkCmd.Parameters.AddWithValue("$id", item.ContentId);
                await unlinkCmd.ExecuteNonQueryAsync(ct);
            }

            await using (var unlinkVoiceAudioCmd = conn.CreateCommand())
            {
                unlinkVoiceAudioCmd.Transaction = tx;
                unlinkVoiceAudioCmd.CommandText =
                    "UPDATE voice_commands SET audio_content_id = NULL WHERE audio_content_id = $id;";
                unlinkVoiceAudioCmd.Parameters.AddWithValue("$id", item.ContentId);
                await unlinkVoiceAudioCmd.ExecuteNonQueryAsync(ct);
            }

            await using (var delCmd = conn.CreateCommand())
            {
                delCmd.Transaction = tx;
                delCmd.CommandText = "DELETE FROM content_refs WHERE content_id = $id;";
                delCmd.Parameters.AddWithValue("$id", item.ContentId);
                await delCmd.ExecuteNonQueryAsync(ct);
            }
        }

        await tx.CommitAsync(ct);

        foreach (var item in expired)
        {
            var fullPath = Path.Combine(_connectionFactory.Options.MediaDirectory, item.RelativePath);
            SecureEraseAndDelete(fullPath);
        }

        return expired.Count;
    }

    internal async Task<StagedEncryptedBlob> StageEncryptedBlobAsync(ContentWriteRequest request, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ContentId);
        var plaintextLength = request.Plaintext.Length;
        var totalDiskBytes = 4 + 12 + 16 + plaintextLength;
        _connectionFactory.EnsureQuotaAllowsWrite(totalDiskBytes);

        var sha256Hex = Convert.ToHexStringLower(SHA256.HashData(request.Plaintext.Span));
        var subFolder = request.CreatedAtUtc.ToUniversalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var fileName = $"{SanitizeFileName(request.ContentId)}.enc";
        var relativePath = Path.Combine(subFolder, fileName);
        var fullPath = Path.Combine(_connectionFactory.Options.MediaDirectory, relativePath);

        var parentDir = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(parentDir);

        var derivedKey = await DeriveContentKeyAsync(request.ContentId, ct);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var tag = new byte[16];
            var output = new byte[totalDiskBytes];

            Buffer.BlockCopy(MagicHeader, 0, output, 0, 4);
            Buffer.BlockCopy(nonce, 0, output, 4, 12);

            using (var aes = new AesGcm(derivedKey, 16))
            {
                aes.Encrypt(nonce, request.Plaintext.Span, output.AsSpan(32), tag, BuildAad(request.ContentId, request.Kind));
            }

            Buffer.BlockCopy(tag, 0, output, 16, 16);

            var tempPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllBytesAsync(tempPath, output, ct);
                File.Move(tempPath, fullPath, overwrite: true);
            }
            catch (IOException ioEx)
            {
                DeletePhysicalBlobQuietly(tempPath);
                throw EncryptedSqliteConnectionFactory.TranslateSqliteException(ioEx, fullPath);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedKey);
        }

        var contentRef = new ContentRef(
            request.ContentId,
            request.Kind,
            sha256Hex,
            plaintextLength,
            request.CreatedAtUtc.ToUniversalTime(),
            request.ExpiresAtUtc?.ToUniversalTime(),
            request.PerceptualHash);

        return new StagedEncryptedBlob(contentRef, relativePath, fullPath);
    }

    internal static async Task UpsertContentRefAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        StagedEncryptedBlob staged,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO content_refs (
                content_id, kind, sha256_hex, plaintext_length, relative_path, ref_count, phash, created_at_utc, expires_at_utc
            ) VALUES (
                $id, $kind, $sha, $len, $relPath, 1, $phash, $createdAt, $expiresAt
            )
            ON CONFLICT(content_id) DO UPDATE SET ref_count = ref_count + 1;
            """;
        cmd.Parameters.AddWithValue("$id", staged.Reference.ContentId);
        cmd.Parameters.AddWithValue("$kind", (int)staged.Reference.Kind);
        cmd.Parameters.AddWithValue("$sha", staged.Reference.Sha256Hex);
        cmd.Parameters.AddWithValue("$len", staged.Reference.PlaintextLength);
        cmd.Parameters.AddWithValue("$relPath", staged.RelativePath);
        cmd.Parameters.AddWithValue("$phash", staged.Reference.PerceptualHash.HasValue
            ? staged.Reference.PerceptualHash.Value.ToString("x16", CultureInfo.InvariantCulture)
            : DBNull.Value);
        cmd.Parameters.AddWithValue("$createdAt", staged.Reference.CreatedAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$expiresAt", staged.Reference.ExpiresAtUtc.HasValue
            ? staged.Reference.ExpiresAtUtc.Value.ToString("O")
            : DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    internal async Task<(int RemainingCount, string? DeletedFullPath)> DecrementRefInTransactionAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        string contentId,
        CancellationToken ct)
    {
        await using var selectCmd = conn.CreateCommand();
        selectCmd.Transaction = tx;
        selectCmd.CommandText = "SELECT ref_count, relative_path FROM content_refs WHERE content_id = $id;";
        selectCmd.Parameters.AddWithValue("$id", contentId);

        int currentCount;
        string relativePath;
        await using (var reader = await selectCmd.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
            {
                return (0, null);
            }

            currentCount = reader.GetInt32(0);
            relativePath = reader.GetString(1);
        }

        var nextCount = Math.Max(0, currentCount - 1);
        if (nextCount == 0)
        {
            await using var delCmd = conn.CreateCommand();
            delCmd.Transaction = tx;
            delCmd.CommandText = "DELETE FROM content_refs WHERE content_id = $id;";
            delCmd.Parameters.AddWithValue("$id", contentId);
            await delCmd.ExecuteNonQueryAsync(ct);
            var fullPath = Path.Combine(_connectionFactory.Options.MediaDirectory, relativePath);
            SecureEraseAndDelete(fullPath);
            return (0, fullPath);
        }

        await using var updCmd = conn.CreateCommand();
        updCmd.Transaction = tx;
        updCmd.CommandText = "UPDATE content_refs SET ref_count = $next WHERE content_id = $id;";
        updCmd.Parameters.AddWithValue("$next", nextCount);
        updCmd.Parameters.AddWithValue("$id", contentId);
        await updCmd.ExecuteNonQueryAsync(ct);
        return (nextCount, null);
    }

    internal static void DeletePhysicalBlobQuietly(string fullPath)
    {
        try
        {
            SecureEraseAndDelete(fullPath);
        }
        catch
        {
            // Best-effort rollback cleanup
        }
    }

    private async Task<byte[]> DeriveContentKeyAsync(string contentId, CancellationToken ct)
    {
        var masterKey = await _keyStore.GetOrCreateMasterKeyAsync(_connectionFactory.Options.MasterKeyId, ct);
        try
        {
            var salt = Encoding.UTF8.GetBytes(contentId);
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 32, salt, HkdfInfo);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    private static byte[] BuildAad(string contentId, ContentKind kind) =>
        Encoding.UTF8.GetBytes($"{contentId}:{(int)kind}");

    private static string SanitizeFileName(string value) =>
        string.Concat(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));

    private static void SecureEraseAndDelete(string fullPath)
    {
        if (!File.Exists(fullPath))
        {
            return;
        }

        var length = new FileInfo(fullPath).Length;
        if (length > 0 && length <= 16 * 1024 * 1024)
        {
            using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Write, FileShare.None);
            Span<byte> zeros = stackalloc byte[4096];
            var remaining = length;
            while (remaining > 0)
            {
                var chunk = (int)Math.Min(zeros.Length, remaining);
                fs.Write(zeros[..chunk]);
                remaining -= chunk;
            }

            fs.Flush(flushToDisk: true);
        }

        File.Delete(fullPath);
    }
}
