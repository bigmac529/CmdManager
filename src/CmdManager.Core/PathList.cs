namespace CmdManager.Core;

/// <summary>
/// Pure manipulation of a Windows PATH-style string ("a;b;c"). Comparison is case-insensitive and ignores surrounding
/// quotes/whitespace and trailing backslashes ("C:\Tools\" == "c:\tools"). Untouched entries keep their exact text
/// (including %VAR% references), so REG_EXPAND_SZ values survive a round trip.
/// </summary>
public static class PathList
{
    public const char Separator = ';';

    /// <summary>Non-empty entries, trimmed.</summary>
    public static IReadOnlyList<string> Split(string? path) =>
        (path ?? string.Empty).Split(Separator).Select(e => e.Trim()).Where(e => e.Length > 0).ToList();

    /// <summary>Canonical form used for comparisons.</summary>
    public static string Normalize(string entry)
    {
        var e = entry.Trim().Trim('"').Trim().Replace('/', '\\');
        while (e.Length > 0 && e.EndsWith('\\') && !IsDriveRoot(e))
            e = e[..^1];
        return e;
    }

    public static bool EntryEquals(string a, string b, Func<string, string>? expand = null)
    {
        if (string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase))
            return true;
        return expand is not null &&
               string.Equals(Normalize(expand(a)), Normalize(expand(b)), StringComparison.OrdinalIgnoreCase);
    }

    public static bool Contains(string? path, string directory, Func<string, string>? expand = null) =>
        Split(path).Any(e => EntryEquals(e, directory, expand));

    /// <summary>Appends <paramref name="directory"/> unless already present; returns the original string when unchanged.</summary>
    public static string Add(string? path, string directory, Func<string, string>? expand = null)
    {
        ValidateDirectory(directory);
        if (Contains(path, directory, expand))
            return path ?? string.Empty;
        var entries = Split(path).ToList();
        entries.Add(Normalize(directory));
        return string.Join(Separator, entries);
    }

    /// <summary>Removes every entry equal to <paramref name="directory"/>; returns the original string when unchanged.</summary>
    public static string Remove(string? path, string directory, Func<string, string>? expand = null)
    {
        if (!Contains(path, directory, expand))
            return path ?? string.Empty;
        return string.Join(Separator, Split(path).Where(e => !EntryEquals(e, directory, expand)));
    }

    /// <summary>Removes <paramref name="oldDirectory"/> (if given) and adds <paramref name="newDirectory"/>.</summary>
    public static string Move(string? path, string? oldDirectory, string newDirectory, Func<string, string>? expand = null)
    {
        var p = path ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(oldDirectory) && !EntryEquals(oldDirectory, newDirectory, expand))
            p = Remove(p, oldDirectory, expand);
        return Add(p, newDirectory, expand);
    }

    /// <summary>Drops empty and duplicate entries (keeping the first occurrence and its original text).</summary>
    public static string Dedupe(string? path, Func<string, string>? expand = null)
    {
        var result = new List<string>();
        foreach (var e in Split(path))
            if (!result.Any(r => EntryEquals(r, e, expand)))
                result.Add(e);
        return string.Join(Separator, result);
    }

    private static bool IsDriveRoot(string e) => e.Length == 3 && e[1] == ':' && e[2] == '\\';

    private static void ValidateDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || Normalize(directory).Length == 0)
            throw new ArgumentException("Directory is required.", nameof(directory));
        if (directory.Contains(Separator))
            throw new ArgumentException("A PATH entry may not contain ';'.", nameof(directory));
    }
}
