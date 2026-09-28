using System.Diagnostics;
using System.IO;
using CmdManager.Core;

namespace CmdManager.Client.Services;

/// <summary>Runs a materialized command from the command folder in a console window (cmd.exe /c or /k).</summary>
public static class CommandRunner
{
    public static void Run(string fullPath, string? arguments, ClientSettings settings, string commandFolder)
    {
        var args = string.IsNullOrWhiteSpace(arguments) ? "" : " " + arguments.Trim();
        var kind = CommandKinds.FromFileName(fullPath);
        var quoted = $"\"{fullPath}\"";
        var inner = kind switch
        {
            CommandKind.Ps1 => $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File {quoted}{args}",
            CommandKind.Csx => $"{settings.CsxRunner} {quoted} --{args}",
            CommandKind.Rdp => $"mstsc.exe {quoted}",
            _ => quoted + args
        };

        // cmd strips the outer quotes of /c "<inner>" when the line has more than two quotes.
        var psi = new ProcessStartInfo("cmd.exe", $"{(settings.KeepRunWindowOpen ? "/k" : "/c")} \"{inner}\"")
        {
            UseShellExecute = false,
            WorkingDirectory = Directory.Exists(commandFolder) ? commandFolder : Path.GetDirectoryName(fullPath)!
        };
        psi.Environment["CMDS"] = commandFolder;
        Process.Start(psi);
    }

    public static void OpenInExternalEditor(string editor, string path)
    {
        var exe = string.IsNullOrWhiteSpace(editor) ? "notepad.exe" : editor;
        Process.Start(new ProcessStartInfo(exe, $"\"{path}\"") { UseShellExecute = true });
    }

    public static void OpenFolder(string folder) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
}
