using System.Security.Cryptography;
using System.Text;

namespace CmdManager.Core;

/// <summary>SHA-256 as lower-case hex. Hashes are always over the exact file bytes (text is UTF-8, no BOM added).</summary>
public static class ContentHash
{
    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static string Sha256Hex(string text) => Sha256Hex(TextContent.ToBytes(text));

    public static string Sha256Hex(Stream stream) => Convert.ToHexStringLower(SHA256.HashData(stream));

    public static async Task<string> Sha256HexOfFileAsync(string path, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, ct));
    }

    public static bool Equal(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Text handling that round-trips byte-exactly: a file is "text" only if it is strict UTF-8 with no NUL characters.
/// A UTF-8 BOM (if present) is kept as U+FEFF inside the string so writing the text back reproduces the same bytes.
/// ANSI/UTF-16 files (e.g. mstsc-saved .rdp) are treated as binary so they are never re-encoded.
/// </summary>
public static class TextContent
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static bool TryDecode(ReadOnlySpan<byte> bytes, out string text)
    {
        text = string.Empty;
        if (bytes.IndexOf((byte)0) >= 0)
            return false;
        try
        {
            text = StrictUtf8.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    public static bool IsText(ReadOnlySpan<byte> bytes) => TryDecode(bytes, out _);

    /// <summary>UTF-8 bytes without adding a BOM (a leading U+FEFF in the string is written as EF BB BF).</summary>
    public static byte[] ToBytes(string text) => StrictUtf8.GetBytes(text);
}
