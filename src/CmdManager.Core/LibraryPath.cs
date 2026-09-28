using System.Diagnostics.CodeAnalysis;

namespace CmdManager.Core;

/// <summary>
/// A validated, normalized library-relative path: forward slashes, no leading slash, no "." / ".." segments,
/// every segment a legal Windows file name. Example: "RDPs/Work PC.rdp" (Folder "RDPs", Name "Work PC.rdp").
/// Identity is case-insensitive (see <see cref="Key"/>), matching the Windows file system it is synced to.
/// </summary>
public sealed record LibraryPath
{
    public const int MaxLength = 400;
    public const int MaxSegmentLength = 255;

    private static readonly char[] InvalidChars = ['<', '>', ':', '"', '|', '?', '*'];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private LibraryPath(string value)
    {
        Value = value;
        var slash = value.LastIndexOf('/');
        Folder = slash < 0 ? string.Empty : value[..slash];
        Name = slash < 0 ? value : value[(slash + 1)..];
    }

    /// <summary>Normalized path, e.g. "RDPs/work.rdp".</summary>
    public string Value { get; }

    /// <summary>Folder part ("" for the library root), forward slashes.</summary>
    public string Folder { get; }

    /// <summary>File name including extension.</summary>
    public string Name { get; }

    /// <summary>Case-insensitive identity used for uniqueness (upper-invariant).</summary>
    public string Key => ToKey(Value);

    public string Extension => Path.GetExtension(Name).TrimStart('.').ToLowerInvariant();

    public string NameWithoutExtension => Path.GetFileNameWithoutExtension(Name);

    public CommandKind Kind => CommandKinds.FromFileName(Name);

    public bool IsInRoot => Folder.Length == 0;

    public override string ToString() => Value;

    public static string ToKey(string normalizedPath) => normalizedPath.ToUpperInvariant();

    public static LibraryPath Combine(string? folder, string name) =>
        Parse(string.IsNullOrWhiteSpace(folder) ? name : folder.TrimEnd('/', '\\') + "/" + name);

    /// <summary>Parses and normalizes; throws <see cref="ArgumentException"/> with a user-readable message when invalid.</summary>
    public static LibraryPath Parse(string? input)
    {
        if (!TryParse(input, out var path, out var error))
            throw new ArgumentException(error, nameof(input));
        return path;
    }

    public static bool TryParse(string? input, [NotNullWhen(true)] out LibraryPath? path, [NotNullWhen(false)] out string? error)
    {
        path = null;
        error = null;
        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Path is required.";
            return false;
        }

        var s = input.Trim().Replace('\\', '/');
        if (s.Length >= 2 && s[1] == ':' || s.StartsWith("//", StringComparison.Ordinal))
        {
            error = "Path must be relative to the library (no drive letter or UNC path).";
            return false;
        }

        while (s.StartsWith("./", StringComparison.Ordinal))
            s = s[2..];
        s = s.TrimStart('/');

        var segments = s.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            error = "Path is required.";
            return false;
        }

        foreach (var seg in segments)
        {
            if (!TryValidateSegment(seg, out error))
                return false;
        }

        var normalized = string.Join('/', segments);
        if (normalized.Length > MaxLength)
        {
            error = $"Path is longer than {MaxLength} characters.";
            return false;
        }

        path = new LibraryPath(normalized);
        return true;
    }

    /// <summary>Validates one file or folder name.</summary>
    public static bool TryValidateSegment(string seg, [NotNullWhen(false)] out string? error)
    {
        error = null;
        if (seg is "." or "..")
            error = "Path may not contain '.' or '..' segments.";
        else if (seg.Length > MaxSegmentLength)
            error = $"'{Trunc(seg)}' is longer than {MaxSegmentLength} characters.";
        else if (seg.IndexOfAny(InvalidChars) >= 0 || seg.Any(char.IsControl))
            error = $"'{seg}' contains a character that is not allowed in Windows file names (<>:\"|?* or control characters).";
        else if (seg.EndsWith('.') || seg.EndsWith(' ') || seg.StartsWith(' '))
            error = $"'{seg}' may not start with a space or end with a space or dot.";
        else if (ReservedNames.Contains(seg.Split('.')[0].TrimEnd()))
            error = $"'{seg}' uses a reserved Windows device name.";
        return error is null;
    }

    private static string Trunc(string s) => s.Length > 40 ? s[..40] + "…" : s;
}
