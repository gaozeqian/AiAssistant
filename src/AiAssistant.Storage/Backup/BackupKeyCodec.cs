using System.Security.Cryptography;
using System.Text;

namespace AiAssistant.Storage.Backup;

/// <summary>
/// Encodes the 256-bit portable-backup key as Crockford Base32. The alphabet omits I, L, O and U so
/// a key that has to be written down or typed cannot be confused with look-alike characters; the
/// decoder additionally maps O→0 and I/L→1 and ignores separators and whitespace.
/// </summary>
public static class BackupKeyCodec
{
    public const int KeySizeBytes = 32;

    private const int EncodedLength = 52;
    private const int GroupSize = 4;
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>Creates a new 256-bit key, formatted in 4-character groups.</summary>
    public static string CreateKey()
    {
        var key = RandomNumberGenerator.GetBytes(KeySizeBytes);
        try
        {
            return Format(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Formats raw key bytes as grouped Crockford Base32 (e.g. <c>7Q3M-9X2K-…</c>).</summary>
    public static string Format(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeySizeBytes)
        {
            throw new ArgumentException($"A backup key must be {KeySizeBytes} bytes.", nameof(key));
        }

        Span<char> encoded = stackalloc char[EncodedLength];
        var buffer = 0;
        var bits = 0;
        var index = 0;
        foreach (var value in key)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                encoded[index++] = Alphabet[(buffer >> (bits - 5)) & 0x1F];
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            encoded[index++] = Alphabet[(buffer << (5 - bits)) & 0x1F];
        }

        var builder = new StringBuilder(EncodedLength + (EncodedLength / GroupSize));
        for (var i = 0; i < index; i++)
        {
            if (i > 0 && i % GroupSize == 0)
            {
                builder.Append('-');
            }

            builder.Append(encoded[i]);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Parses a user-entered key. Case, separators and the common look-alike substitutions are
    /// tolerated; the 4 padding bits of the last character must be zero so a mistyped tail fails
    /// instead of silently decoding to a different key.
    /// </summary>
    public static byte[] Parse(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var normalized = Normalize(key);
        if (normalized.Length != EncodedLength)
        {
            throw new ArgumentException("备份密钥长度不正确。", nameof(key));
        }

        var bytes = new byte[KeySizeBytes];
        var buffer = 0;
        var bits = 0;
        var index = 0;
        foreach (var character in normalized)
        {
            var value = Alphabet.IndexOf(character);
            if (value < 0)
            {
                throw new ArgumentException("备份密钥包含无效字符。", nameof(key));
            }

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bytes[index++] = (byte)((buffer >> (bits - 8)) & 0xFF);
                bits -= 8;
            }
        }

        if (bits > 0 && (buffer & ((1 << bits) - 1)) != 0)
        {
            throw new ArgumentException("备份密钥校验位不正确。", nameof(key));
        }

        if (index != KeySizeBytes)
        {
            throw new ArgumentException("备份密钥长度不正确。", nameof(key));
        }

        return bytes;
    }

    private static string Normalize(string key)
    {
        var builder = new StringBuilder(key.Length);
        foreach (var raw in key)
        {
            if (raw is '-' or ' ' or '\t' or '\r' or '\n')
            {
                continue;
            }

            var character = char.ToUpperInvariant(raw);
            character = character switch
            {
                'O' => '0',
                'I' => '1',
                'L' => '1',
                _ => character
            };
            builder.Append(character);
        }

        return builder.ToString();
    }
}
