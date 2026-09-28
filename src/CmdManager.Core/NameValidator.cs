using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace CmdManager.Core;

/// <summary>
/// Rules for the short commandlet names you type at the prompt (e.g. "g", "cdCMDs", "cmd-admin", "3dp").
/// Stricter than <see cref="LibraryPath"/>: no spaces, no extension, starts with a letter or digit.
/// </summary>
public static partial class NameValidator
{
    public const int MaxNameLength = 64;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.+\\-]*$")]
    private static partial Regex CommandNamePattern();

    /// <summary>Names of cmd.exe built-ins that would shadow (or be shadowed by) a same-named .cmd on PATH.</summary>
    private static readonly HashSet<string> CmdBuiltins = new(StringComparer.OrdinalIgnoreCase)
    {
        "assoc", "break", "call", "cd", "chdir", "cls", "color", "copy", "date", "del", "dir", "echo", "endlocal",
        "erase", "exit", "for", "ftype", "goto", "if", "md", "mkdir", "mklink", "move", "path", "pause", "popd",
        "prompt", "pushd", "rd", "rem", "ren", "rename", "rmdir", "set", "setlocal", "shift", "start", "time",
        "title", "type", "ver", "verify", "vol"
    };

    public static bool TryValidateCommandName(string? name, [NotNullWhen(false)] out string? error)
    {
        error = null;
        name = name?.Trim();
        if (string.IsNullOrEmpty(name))
            error = "Name is required.";
        else if (name.Length > MaxNameLength)
            error = $"Name is longer than {MaxNameLength} characters.";
        else if (!CommandNamePattern().IsMatch(name))
            error = "Use letters, digits, '-', '_', '+' or '.', starting with a letter or digit (no spaces).";
        else if (name.EndsWith('.'))
            error = "Name may not end with a dot.";
        else if (CmdBuiltins.Contains(name))
            error = $"'{name}' is a cmd.exe built-in command; cmd would run the built-in instead.";
        else if (!LibraryPath.TryValidateSegment(name, out var segError))
            error = segError;
        return error is null;
    }

    public static bool IsBuiltin(string name) => CmdBuiltins.Contains(name);
}
