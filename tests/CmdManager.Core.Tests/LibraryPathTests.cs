using CmdManager.Core;

namespace CmdManager.Core.Tests;

public class LibraryPathTests
{
    [Theory]
    [InlineData("g.cmd", "g.cmd", "", "g.cmd")]
    [InlineData("RDPs\\Work PC.rdp", "RDPs/Work PC.rdp", "RDPs", "Work PC.rdp")]
    [InlineData("/CSXScripts//Settings.json", "CSXScripts/Settings.json", "CSXScripts", "Settings.json")]
    [InlineData("./a/b/c.ps1", "a/b/c.ps1", "a/b", "c.ps1")]
    [InlineData("  x.csx  ", "x.csx", "", "x.csx")]
    public void Normalizes(string input, string value, string folder, string name)
    {
        var p = LibraryPath.Parse(input);
        Assert.Equal(value, p.Value);
        Assert.Equal(folder, p.Folder);
        Assert.Equal(name, p.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..\\x.cmd")]
    [InlineData("a/../x.cmd")]
    [InlineData("a/./x.cmd")]
    [InlineData("C:\\Windows\\x.cmd")]
    [InlineData("\\\\server\\share\\x.cmd")]
    [InlineData("a<b.cmd")]
    [InlineData("what?.cmd")]
    [InlineData("pipe|.cmd")]
    [InlineData("trailingdot.")]
    [InlineData("folder /x.cmd")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("sub/COM1.cmd")]
    [InlineData("lpt9")]
    public void Rejects(string? input)
    {
        Assert.False(LibraryPath.TryParse(input, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
        Assert.Throws<ArgumentException>(() => LibraryPath.Parse(input));
    }

    [Fact]
    public void Rejects_too_long()
    {
        Assert.False(LibraryPath.TryParse(new string('a', 256) + ".cmd", out _, out _));
        var deep = string.Join('/', Enumerable.Repeat(new string('d', 50), 9)) + "/x.cmd";
        Assert.False(LibraryPath.TryParse(deep, out _, out _));
    }

    [Fact]
    public void Key_is_case_insensitive_identity()
    {
        Assert.Equal(LibraryPath.Parse("rdps/Work.RDP").Key, LibraryPath.Parse("RDPs\\work.rdp").Key);
    }

    [Theory]
    [InlineData("g.cmd", CommandKind.Cmd)]
    [InlineData("x.BAT", CommandKind.Bat)]
    [InlineData("New.csx", CommandKind.Csx)]
    [InlineData("a/b.ps1", CommandKind.Ps1)]
    [InlineData("7z.lnk", CommandKind.Lnk)]
    [InlineData("curl.exe", CommandKind.Exe)]
    [InlineData("RDPs/x.rdp", CommandKind.Rdp)]
    [InlineData("Settings.json", CommandKind.Other)]
    [InlineData("README", CommandKind.Other)]
    public void Kind_from_extension(string path, CommandKind kind) => Assert.Equal(kind, LibraryPath.Parse(path).Kind);

    [Fact]
    public void Combine_joins_folder_and_name()
    {
        Assert.Equal("RDPs/a.rdp", LibraryPath.Combine("RDPs\\", "a.rdp").Value);
        Assert.Equal("a.rdp", LibraryPath.Combine("", "a.rdp").Value);
    }
}

public class NameValidatorTests
{
    [Theory]
    [InlineData("g")]
    [InlineData("cdCMDs")]
    [InlineData("cmd-admin")]
    [InlineData("3dp")]
    [InlineData("srcPAX")]
    [InlineData("my_tool.v2")]
    public void Accepts_commandlet_names(string name) => Assert.True(NameValidator.TryValidateCommandName(name, out _));

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("-dash")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("x:")]
    [InlineData("dot.")]
    [InlineData("con")]
    [InlineData("dir")]    // cmd built-in
    [InlineData("START")]  // cmd built-in
    public void Rejects_bad_names(string name)
    {
        Assert.False(NameValidator.TryValidateCommandName(name, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Rejects_too_long() => Assert.False(NameValidator.TryValidateCommandName(new string('a', 65), out _));
}
