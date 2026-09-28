namespace CmdManager.Core;

/// <summary>A file produced by a template.</summary>
public sealed record TemplateFile(string FileName, string Content);

public sealed record TemplateResult(IReadOnlyList<TemplateFile> Files, string? Note = null);

public enum CommandTemplateKind
{
    /// <summary>CSXScripts New.cs "cmd" content: <c>start "" "&lt;target&gt;"</c> then <c>exit</c>.</summary>
    StartCmd,

    /// <summary>
    /// C# scriptlet: <c>name.csx</c> plus a <c>name.cmd</c> wrapper that runs it with the configured runner
    /// (default <c>dotnet script</c>; legacy <c>scriptcs</c> as in MkNewCmd), located via <c>%~dp0</c>.
    /// </summary>
    CsxScriptlet,

    /// <summary>CSXScripts (2018) style: <c>name.csx</c> that references CSXScripts/CSXScripts.dll and calls <c>name.Main</c>, plus wrapper.</summary>
    CsxLibrary,

    /// <summary>PowerShell script plus a .cmd wrapper so it can be typed at the prompt.</summary>
    PowerShellPair,

    /// <summary>Plain batch file.</summary>
    EmptyCmd
}

/// <summary>
/// Templates for new commandlets, carried over from ExeLibrary/MkNewCmd/MkNewCmd.cs and CSXScripts/CS/New.cs.
/// All content uses CRLF line endings (these files run under cmd.exe / scriptcs / PowerShell on Windows).
/// </summary>
public static class CommandTemplates
{
    public const string NewLine = "\r\n";

    public static IReadOnlyList<(CommandTemplateKind Kind, string Title)> All { get; } =
    [
        (CommandTemplateKind.CsxScriptlet, "C# scriptlet (.csx) + .cmd wrapper"),
        (CommandTemplateKind.StartCmd, "Launcher .cmd  (start \"\" \"target\" / exit)"),
        (CommandTemplateKind.CsxLibrary, ".csx calling CSXScripts.dll (CSXScripts New) + .cmd wrapper"),
        (CommandTemplateKind.PowerShellPair, "PowerShell .ps1 + .cmd wrapper"),
        (CommandTemplateKind.EmptyCmd, "Empty .cmd")
    ];

    /// <summary>Builds the files for a new command named <paramref name="name"/> (validated with <see cref="NameValidator"/>).</summary>
    /// <param name="target">For <see cref="CommandTemplateKind.StartCmd"/>: program/URL/folder to start (may be empty).</param>
    /// <param name="csxRunner">Runner command for .csx wrappers, e.g. "dotnet script" (default) or "scriptcs".</param>
    public static TemplateResult Create(CommandTemplateKind kind, string name, string? target = null, string? csxRunner = null)
    {
        var runner = string.IsNullOrWhiteSpace(csxRunner) ? DefaultCsxRunner : csxRunner.Trim();
        if (!NameValidator.TryValidateCommandName(name, out var error))
            throw new ArgumentException(error, nameof(name));
        name = name.Trim();

        return kind switch
        {
            CommandTemplateKind.StartCmd => new([new($"{name}.cmd", StartCmd(target))]),
            CommandTemplateKind.CsxScriptlet => new(
                [new($"{name}.csx", IsScriptCs(runner) ? ScriptCsCsx() : DotnetScriptCsx(name)), new($"{name}.cmd", CsxWrapperCmd(name, runner))],
                IsScriptCs(runner)
                    ? "Legacy scriptcs runner (MkNewCmd). scriptcs must be on PATH."
                    : "Runs with dotnet-script (dotnet tool install -g dotnet-script). Type the name at the prompt."),
            CommandTemplateKind.CsxLibrary => new(
                [new($"{name}.csx", CsxLibrary(name, IsScriptCs(runner))), new($"{name}.cmd", CsxWrapperCmd(name, runner))],
                $"Expects a class '{name}' with a static Main(params string[]) in CSXScripts/CSXScripts.dll (see CsxLibraryClass). CSXScripts.dll targets .NET Framework, so the scriptcs runner is the safe choice."),
            CommandTemplateKind.PowerShellPair => new(
                [new($"{name}.cmd", PowerShellCmd(name)), new($"{name}.ps1", PowerShellScript(name))]),
            CommandTemplateKind.EmptyCmd => new([new($"{name}.cmd", "@echo off" + NewLine)]),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    /// <summary>CSXScripts New.CmdContent, with an optional target and argument pass-through.</summary>
    public static string StartCmd(string? target)
    {
        var t = (target ?? string.Empty).Trim().Trim('"');
        var line = t.Length == 0 ? "start \"\" \"\"" : $"start \"\" \"{t}\" %*";
        return line + NewLine + NewLine + "exit" + NewLine;
    }

    public const string DefaultCsxRunner = "dotnet script";

    public static bool IsScriptCs(string runner) => runner.Trim().StartsWith("scriptcs", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Wrapper so "name args" works at the prompt. The script is found next to the wrapper (<c>%~dp0</c>), not via %CMDS%.
    /// dotnet script: <c>dotnet script "%~dp0name.csx" -- %*</c>; scriptcs (MkNewCmd): <c>scriptcs "%~dp0name.csx" -- %1 … %9</c>.
    /// </summary>
    public static string CsxWrapperCmd(string name, string? runner = null)
    {
        runner = string.IsNullOrWhiteSpace(runner) ? DefaultCsxRunner : runner.Trim();
        var args = IsScriptCs(runner) ? "%1 %2 %3 %4 %5 %6 %7 %8 %9" : "%*";
        return "@echo off" + NewLine +
               $"{runner} \"%~dp0{name}.csx\" -- {args}" + NewLine +
               "exit /b %ERRORLEVEL%" + NewLine;
    }

    /// <summary>MkNewCmd's legacy wrapper line (kept for reference; uses %CMDS%).</summary>
    public static string LegacyMkNewCmd(string name) =>
        $"scriptcs \"%CMDS%\\{name}.csx\" -- %1 %2 %3 %4 %5 %6 %7 %8 %9" + NewLine + "exit" + NewLine;

    /// <summary>Starter C# scriptlet for dotnet-script (arguments are in <c>Args</c>).</summary>
    public static string DotnetScriptCsx(string name) => string.Join(NewLine,
        $"// {name}.csx - C# scriptlet run by dotnet-script. At the prompt:  {name} arg1 arg2",
        "//",
        "// References (uncomment as needed):",
        "//   #r \"nuget: Newtonsoft.Json, 13.0.3\"      NuGet package",
        "//   #r \"CSXScripts/CSXScripts.dll\"           assembly relative to this script",
        "//   #load \"shared/helpers.csx\"               another script",
        "using System;",
        "using System.Diagnostics;",
        "using System.IO;",
        "using System.Linq;",
        "",
        "if (Args.Count == 0)",
        "{",
        $"    Console.WriteLine(\"usage: {name} <text>\");",
        "    return;",
        "}",
        "",
        "var text = string.Join(\" \", Args);",
        "Console.WriteLine($\"You said: {text}\");",
        "",
        "// Example: open a URL built from the arguments",
        "// Process.Start(new ProcessStartInfo($\"https://www.google.com/search?q={Uri.EscapeDataString(text)}\") { UseShellExecute = true });",
        "");

    /// <summary>MkNewCmd's starter .csx.</summary>
    public static string ScriptCsCsx() =>
        "using System;" + NewLine +
        NewLine +
        "var arg1 = Env.ScriptArgs[0];" + NewLine +
        "var arg2 = Env.ScriptArgs[1];" + NewLine;

    /// <summary>The CMDs-repo .csx shape (e.g. New.csx, G.csx): reference the library dll and call {name}.Main.</summary>
    public static string CsxLibrary(string name, bool scriptCs = true) =>
        "#r \"CSXScripts/CSXScripts.dll\"" + NewLine +
        "using CSXScripts;" + NewLine +
        $"{name}.Main({(scriptCs ? "Env.ScriptArgs" : "Args")}.ToArray());" + NewLine;

    /// <summary>CSXScripts New.CsContent: the companion class to add to the CSXScripts project (CS/{name}.cs).</summary>
    public static string CsxLibraryClass(string name) => string.Join(NewLine,
        "using CSXScripts.Utility;",
        "using System;",
        "using System.Collections.Generic;",
        "using System.Diagnostics;",
        "using System.IO;",
        "using System.Linq;",
        "",
        "namespace CSXScripts",
        "{",
        "    [CsxScript]",
        $"    public class {name}",
        "    {",
        "        public static void Main(params string[] scriptArgs)",
        "        {",
        "            var args = new CsxArguments(scriptArgs);",
        "",
        "            Prompt.WriteLn(\"Hello World!\");",
        "",
        "            Util.EndScript();",
        "        }",
        "    }",
        "}",
        "");

    public static string PowerShellCmd(string name) =>
        "@echo off" + NewLine +
        $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"%~dp0{name}.ps1\" %*" + NewLine;

    public static string PowerShellScript(string name) => string.Join(NewLine,
        "param(",
        "    [Parameter(ValueFromRemainingArguments = $true)]",
        "    [string[]] $Rest",
        ")",
        "",
        "$ErrorActionPreference = 'Stop'",
        $"Write-Host \"{name}: $($Rest -join ' ')\"",
        "");
}
