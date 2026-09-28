namespace CmdManager.Core;

using CmdManager.Core.Contracts;

/// <summary>
/// Decides how a file from an imported folder (e.g. a clone of bigmac529/CMDs) is stored:
/// <list type="number">
/// <item>Script kinds (.cmd .bat .csx .ps1 .rdp) anywhere, and .exe/.lnk in the library root (they are on PATH) → Command.</item>
/// <item>Any other file that is strict UTF-8 text (Settings.json, README.md, ...) → Command of kind Other (editable in-app).</item>
/// <item>Everything else (dlls, SysTools exes, images, ...) → Asset (binary).</item>
/// </list>
/// Commands whose bytes are not strict UTF-8 (ANSI .cmd, UTF-16 .rdp, .lnk, .exe) keep their exact bytes as a binary blob.
/// </summary>
public static class ImportClassifier
{
    public static readonly IReadOnlySet<string> SkippedDirectories =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git", ".vs" };

    public static readonly IReadOnlySet<string> SkippedFiles =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "desktop.ini", "Thumbs.db" };

    public static bool IsSkippedDirectory(string directoryName) => SkippedDirectories.Contains(directoryName);

    public static bool IsSkippedFile(string fileName) => SkippedFiles.Contains(fileName);

    public static LibraryItemType Classify(LibraryPath path, ReadOnlySpan<byte> content)
    {
        var kind = path.Kind;
        if (CommandKinds.IsScriptKind(kind))
            return LibraryItemType.Command;
        if (kind is CommandKind.Exe or CommandKind.Lnk)
            return path.IsInRoot ? LibraryItemType.Command : LibraryItemType.Asset;
        return TextContent.IsText(content) ? LibraryItemType.Command : LibraryItemType.Asset;
    }

    /// <summary>
    /// Enumerates importable files under <paramref name="root"/> (skipping bin/obj/.git/.vs and desktop.ini),
    /// returning (absolute path, library path). Files whose names are not valid library paths are returned with an error.
    /// </summary>
    public static IEnumerable<(string FullPath, LibraryPath? Path, string? Error)> EnumerateFolder(string root)
    {
        var rootFull = Path.GetFullPath(root);
        var stack = new Stack<string>();
        stack.Push(rootFull);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var sub in Directory.EnumerateDirectories(dir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).Reverse())
            {
                var info = new DirectoryInfo(sub);
                if (IsSkippedDirectory(info.Name) || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    continue;
                stack.Push(sub);
            }

            foreach (var file in Directory.EnumerateFiles(dir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(file);
                if (IsSkippedFile(name))
                    continue;
                var rel = Path.GetRelativePath(rootFull, file);
                if (LibraryPath.TryParse(rel, out var lp, out var error))
                    yield return (file, lp, null);
                else
                    yield return (file, null, error);
            }
        }
    }
}
