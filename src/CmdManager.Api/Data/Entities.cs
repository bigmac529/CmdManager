using CmdManager.Core;

namespace CmdManager.Api.Data;

public sealed class User
{
    public int Id { get; set; }
    public string UserName { get; set; } = "";
    /// <summary>Upper-invariant user name; unique.</summary>
    public string NormalizedUserName { get; set; } = "";
    public string? Email { get; set; }
    /// <summary>ASP.NET Core Identity PasswordHasher v3 format (PBKDF2-HMAC-SHA512, 100k iterations, random salt).</summary>
    public string PasswordHash { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime? LastLoginUtc { get; set; }
    public int AccessFailedCount { get; set; }
    public DateTime? LockoutEndUtc { get; set; }
    public bool IsDisabled { get; set; }

    public List<RefreshToken> RefreshTokens { get; set; } = [];
}

public sealed class RefreshToken
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User? User { get; set; }
    /// <summary>SHA-256 of the opaque token handed to the client (the token itself is never stored).</summary>
    public string TokenHash { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public DateTime? RevokedUtc { get; set; }
}

/// <summary>Columns shared by commands and assets: a file at a library-relative path owned by one user.</summary>
public abstract class LibraryFile
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User? User { get; set; }
    /// <summary>File name with extension, e.g. "g.cmd".</summary>
    public string Name { get; set; } = "";
    /// <summary>Folder relative to the library root ("" = root), forward slashes, e.g. "RDPs".</summary>
    public string Folder { get; set; } = "";
    /// <summary>Folder + "/" + Name, e.g. "RDPs/work.rdp".</summary>
    public string RelativePath { get; set; } = "";
    /// <summary>Upper-invariant RelativePath; unique per user (case-insensitive identity on any collation).</summary>
    public string PathKey { get; set; } = "";
    public string? Description { get; set; }
    public long Size { get; set; }
    /// <summary>Lower-case hex SHA-256 of the file bytes.</summary>
    public string Sha256 { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public void SetPath(LibraryPath path)
    {
        Name = path.Name;
        Folder = path.Folder;
        RelativePath = path.Value;
        PathKey = path.Key;
    }
}

/// <summary>A commandlet (.cmd/.bat/.csx/.ps1/.lnk/.exe/.rdp/other). Text in TextContent, or bytes in BinaryContent.</summary>
public sealed class Command : LibraryFile
{
    public CommandKind Kind { get; set; }
    /// <summary>Tags joined with ';'.</summary>
    public string? Tags { get; set; }
    public bool IsBinary { get; set; }
    public string? TextContent { get; set; }
    public byte[]? BinaryContent { get; set; }

    public byte[] ContentBytes() => IsBinary ? BinaryContent ?? [] : TextContent is null ? [] : Core.TextContent.ToBytes(TextContent);

    public void SetText(string text)
    {
        IsBinary = false;
        TextContent = text;
        BinaryContent = null;
        var bytes = Core.TextContent.ToBytes(text);
        Size = bytes.LongLength;
        Sha256 = ContentHash.Sha256Hex(bytes);
    }

    public void SetBinary(byte[] bytes)
    {
        IsBinary = true;
        TextContent = null;
        BinaryContent = bytes;
        Size = bytes.LongLength;
        Sha256 = ContentHash.Sha256Hex(bytes);
    }
}

/// <summary>A supporting binary file (dll, exe, image, ...), stored as varbinary(max).</summary>
public sealed class Asset : LibraryFile
{
    public byte[] Content { get; set; } = [];

    public void SetContent(byte[] bytes)
    {
        Content = bytes;
        Size = bytes.LongLength;
        Sha256 = ContentHash.Sha256Hex(bytes);
    }
}
