using CmdManager.Core;

namespace CmdManager.Core.Tests;

public class TemplateTests
{
    [Fact]
    public void Csx_scriptlet_defaults_to_dotnet_script_with_dp0_wrapper()
    {
        var r = CommandTemplates.Create(CommandTemplateKind.CsxScriptlet, "hello");
        Assert.Equal(["hello.csx", "hello.cmd"], r.Files.Select(f => f.FileName).ToArray());
        var cmd = r.Files[1].Content;
        Assert.Contains("dotnet script \"%~dp0hello.csx\" -- %*", cmd);
        Assert.DoesNotContain("%CMDS%", cmd);
        var csx = r.Files[0].Content;
        Assert.Contains("Args", csx);
        Assert.Contains("#r \"nuget:", csx);
        Assert.Contains("using System;", csx);
        Assert.DoesNotContain("Env.ScriptArgs", csx);
    }

    [Fact]
    public void Csx_scriptlet_with_scriptcs_runner_matches_MkNewCmd_starter()
    {
        var r = CommandTemplates.Create(CommandTemplateKind.CsxScriptlet, "old", csxRunner: "scriptcs");
        Assert.Contains("scriptcs \"%~dp0old.csx\" -- %1 %2 %3 %4 %5 %6 %7 %8 %9", r.Files[1].Content);
        Assert.Equal("using System;\r\n\r\nvar arg1 = Env.ScriptArgs[0];\r\nvar arg2 = Env.ScriptArgs[1];\r\n", r.Files[0].Content);
    }

    [Fact]
    public void Custom_runner_is_used_verbatim()
    {
        var cmd = CommandTemplates.CsxWrapperCmd("x", "C:\\tools\\dotnet-script.exe");
        Assert.Contains("C:\\tools\\dotnet-script.exe \"%~dp0x.csx\" -- %*", cmd);
    }

    [Fact]
    public void Start_cmd_matches_CSXScripts_New()
    {
        Assert.Equal("start \"\" \"\"\r\n\r\nexit\r\n", CommandTemplates.StartCmd(null));
        Assert.Equal("start \"\" \"C:\\Tools\\x.exe\" %*\r\n\r\nexit\r\n", CommandTemplates.StartCmd("\"C:\\Tools\\x.exe\""));
    }

    [Fact]
    public void Csx_library_references_CSXScripts_dll()
    {
        var r = CommandTemplates.Create(CommandTemplateKind.CsxLibrary, "G", csxRunner: "scriptcs");
        Assert.Equal("#r \"CSXScripts/CSXScripts.dll\"\r\nusing CSXScripts;\r\nG.Main(Env.ScriptArgs.ToArray());\r\n", r.Files[0].Content);
        Assert.Contains("public class G", CommandTemplates.CsxLibraryClass("G"));
    }

    [Fact]
    public void Powershell_pair_and_empty()
    {
        var ps = CommandTemplates.Create(CommandTemplateKind.PowerShellPair, "p");
        Assert.Contains("-File \"%~dp0p.ps1\" %*", ps.Files[0].Content);
        Assert.Equal("p.ps1", ps.Files[1].FileName);
        Assert.Equal("@echo off\r\n", CommandTemplates.Create(CommandTemplateKind.EmptyCmd, "e").Files[0].Content);
    }

    [Fact]
    public void Legacy_MkNewCmd_line_is_preserved_for_reference()
    {
        Assert.Equal("scriptcs \"%CMDS%\\x.csx\" -- %1 %2 %3 %4 %5 %6 %7 %8 %9\r\nexit\r\n", CommandTemplates.LegacyMkNewCmd("x"));
    }

    [Fact]
    public void All_templates_use_crlf_only()
    {
        foreach (var (kind, _) in CommandTemplates.All)
            foreach (var f in CommandTemplates.Create(kind, "t").Files)
                Assert.DoesNotMatch("(?<!\r)\n", f.Content);
    }

    [Fact]
    public void Invalid_name_throws() => Assert.Throws<ArgumentException>(() => CommandTemplates.Create(CommandTemplateKind.EmptyCmd, "bad name"));
}
