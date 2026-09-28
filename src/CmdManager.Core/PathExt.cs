namespace CmdManager.Core;

/// <summary>
/// Pure manipulation of PATHEXT (".COM;.EXE;.BAT;.CMD;..."). A user-level PATHEXT <b>replaces</b> the machine value
/// (it is not appended like PATH), so adding an extension per user means writing machine value + user extras + the
/// new extension. Comparisons are case-insensitive; entries are normalized to upper case with a leading dot.
/// </summary>
public static class PathExt
{
    public const string Csx = ".CSX";

    /// <summary>Windows' built-in default, used when the machine value cannot be read.</summary>
    public const string WindowsDefault = ".COM;.EXE;.BAT;.CMD;.VBS;.VBE;.JS;.JSE;.WSF;.WSH;.MSC";

    public static IReadOnlyList<string> Split(string? value)
    {
        var result = new List<string>();
        foreach (var raw in (value ?? string.Empty).Split(';'))
        {
            var e = NormalizeExtension(raw);
            if (e.Length > 1 && !result.Contains(e, StringComparer.OrdinalIgnoreCase))
                result.Add(e);
        }
        return result;
    }

    public static string NormalizeExtension(string ext)
    {
        var e = ext.Trim().Trim('"').Trim();
        if (e.Length == 0)
            return string.Empty;
        return (e.StartsWith('.') ? e : "." + e).ToUpperInvariant();
    }

    /// <summary>The PATHEXT a process of this user actually sees.</summary>
    public static string Effective(string? machineValue, string? userValue) =>
        !string.IsNullOrWhiteSpace(userValue) ? userValue! : (string.IsNullOrWhiteSpace(machineValue) ? WindowsDefault : machineValue!);

    public static bool Contains(string? value, string extension) =>
        Split(value).Contains(NormalizeExtension(extension), StringComparer.OrdinalIgnoreCase);

    public static bool IsEffective(string? machineValue, string? userValue, string extension) =>
        Contains(Effective(machineValue, userValue), extension);

    /// <summary>
    /// New user-level PATHEXT that makes <paramref name="extension"/> executable, or null when nothing needs to change.
    /// Result = machine entries, then user-only entries, then the extension; deduplicated case-insensitively.
    /// </summary>
    public static string? WithExtension(string? machineValue, string? userValue, string extension = Csx)
    {
        if (IsEffective(machineValue, userValue, extension))
            return null;
        var machine = string.IsNullOrWhiteSpace(machineValue) ? WindowsDefault : machineValue;
        return Join(Split(machine).Concat(Split(userValue)).Append(NormalizeExtension(extension)));
    }

    /// <summary>
    /// User-level PATHEXT after removing <paramref name="extension"/>. Returns null when the remainder adds nothing to
    /// the machine value (i.e. the user variable should be deleted); returns <paramref name="userValue"/> unchanged when
    /// the extension is not in it.
    /// </summary>
    public static string? WithoutExtension(string? machineValue, string? userValue, string extension = Csx)
    {
        if (string.IsNullOrWhiteSpace(userValue))
            return null;
        if (!Contains(userValue, extension))
            return userValue;
        var remaining = Split(userValue).Where(e => !e.Equals(NormalizeExtension(extension), StringComparison.OrdinalIgnoreCase)).ToList();
        var machine = Split(string.IsNullOrWhiteSpace(machineValue) ? WindowsDefault : machineValue);
        if (remaining.Count == 0 || SameSet(remaining, machine))
            return null;
        return Join(remaining);
    }

    public static string Join(IEnumerable<string> entries) =>
        string.Join(';', entries.Select(NormalizeExtension).Where(e => e.Length > 1).Distinct(StringComparer.OrdinalIgnoreCase));

    private static bool SameSet(IReadOnlyCollection<string> a, IReadOnlyCollection<string> b) =>
        a.Count == b.Count && a.All(x => b.Contains(x, StringComparer.OrdinalIgnoreCase));
}
