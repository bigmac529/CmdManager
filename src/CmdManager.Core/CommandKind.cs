namespace CmdManager.Core;

/// <summary>What kind of commandlet a command is (derived from its file extension).</summary>
public enum CommandKind
{
    Other = 0,
    Cmd,
    Bat,
    Csx,
    Ps1,
    Lnk,
    Exe,
    Rdp
}

public static class CommandKinds
{
    /// <summary>Maps a file name or extension (".cmd", "cmd", "g.cmd") to a <see cref="CommandKind"/>.</summary>
    public static CommandKind FromFileName(string fileNameOrExtension)
    {
        var ext = Path.GetExtension(fileNameOrExtension);
        if (string.IsNullOrEmpty(ext))
            ext = fileNameOrExtension.Contains('.') ? string.Empty : fileNameOrExtension;
        return ext.TrimStart('.').ToLowerInvariant() switch
        {
            "cmd" => CommandKind.Cmd,
            "bat" => CommandKind.Bat,
            "csx" => CommandKind.Csx,
            "ps1" => CommandKind.Ps1,
            "lnk" => CommandKind.Lnk,
            "exe" => CommandKind.Exe,
            "rdp" => CommandKind.Rdp,
            _ => CommandKind.Other
        };
    }

    /// <summary>Lower-case extension without the dot, or null for <see cref="CommandKind.Other"/>.</summary>
    public static string? Extension(CommandKind kind) => kind switch
    {
        CommandKind.Other => null,
        _ => kind.ToString().ToLowerInvariant()
    };

    /// <summary>Kinds that cmd.exe / the shell can launch directly by path.</summary>
    public static bool IsRunnable(CommandKind kind) => kind != CommandKind.Other;

    /// <summary>Script kinds that are commands wherever they live (e.g. PSScripts/x.ps1, RDPs/x.rdp).</summary>
    public static bool IsScriptKind(CommandKind kind) =>
        kind is CommandKind.Cmd or CommandKind.Bat or CommandKind.Csx or CommandKind.Ps1 or CommandKind.Rdp;
}
