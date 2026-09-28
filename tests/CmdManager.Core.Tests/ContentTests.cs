using CmdManager.Core;
using CmdManager.Core.Contracts;

namespace CmdManager.Core.Tests;

public class ContentTests
{
    [Fact]
    public void Sha256_of_known_value()
    {
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", ContentHash.Sha256Hex("abc"u8));
        Assert.Equal(ContentHash.Sha256Hex("abc"u8), ContentHash.Sha256Hex("abc"));
        Assert.True(ContentHash.Equal("ABC", "abc"));
    }

    [Fact]
    public void Text_roundtrip_is_byte_exact_including_bom_and_crlf()
    {
        byte[] withBom = [0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i', 0x0D, 0x0A, 0xC3, 0xA9];
        Assert.True(TextContent.TryDecode(withBom, out var text));
        Assert.Equal('\uFEFF', text[0]);
        Assert.Equal(withBom, TextContent.ToBytes(text));
    }

    [Fact]
    public void No_bom_is_added()
    {
        Assert.Equal("exit"u8.ToArray(), TextContent.ToBytes("exit"));
    }

    [Theory]
    [InlineData(new byte[] { 0x65, 0xE9 })]                   // Windows-1252 "eé"
    [InlineData(new byte[] { 0xFF, 0xFE, 0x61, 0x00 })]       // UTF-16LE with BOM
    [InlineData(new byte[] { 0x4D, 0x5A, 0x90, 0x00 })]       // PE header
    [InlineData(new byte[] { 0x61, 0x00, 0x62 })]             // NUL byte
    public void Non_utf8_or_nul_is_binary(byte[] bytes) => Assert.False(TextContent.IsText(bytes));

    [Fact]
    public void Empty_is_text() => Assert.True(TextContent.IsText([]));
}

public class ImportClassifierTests
{
    private static LibraryItemType Classify(string path, byte[] bytes) => ImportClassifier.Classify(LibraryPath.Parse(path), bytes);
    private static readonly byte[] Text = "hello"u8.ToArray();
    private static readonly byte[] Binary = [0x4D, 0x5A, 0x00, 0x01];

    [Theory]
    [InlineData("g.cmd")]
    [InlineData("Setup/install.bat")]
    [InlineData("New.csx")]
    [InlineData("PSScripts/x.ps1")]
    [InlineData("RDPs/work.rdp")]
    public void Script_kinds_are_commands_anywhere_even_if_binary(string path)
    {
        Assert.Equal(LibraryItemType.Command, Classify(path, Text));
        Assert.Equal(LibraryItemType.Command, Classify(path, Binary));
    }

    [Fact]
    public void Exe_and_lnk_are_commands_only_in_root()
    {
        Assert.Equal(LibraryItemType.Command, Classify("GitLatest.exe", Binary));
        Assert.Equal(LibraryItemType.Command, Classify("7z.lnk", Binary));
        Assert.Equal(LibraryItemType.Asset, Classify("SysTools/procexp.exe", Binary));
        Assert.Equal(LibraryItemType.Asset, Classify("SysTools/x.lnk", Binary));
    }

    [Fact]
    public void Other_text_is_command_other_binary_is_asset()
    {
        Assert.Equal(LibraryItemType.Command, Classify("CSXScripts/Settings.json", Text));
        Assert.Equal(LibraryItemType.Asset, Classify("CSXScripts/CSXScripts.dll", Binary));
    }

    [Fact]
    public void EnumerateFolder_skips_build_and_vcs_folders_and_desktop_ini()
    {
        var root = Path.Combine(Path.GetTempPath(), "cmdlib-enum-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var rel in new[] { "a.cmd", "RDPs/x.rdp", "bin/b.dll", "obj/o.txt", ".git/HEAD", ".vs/v.json", "sub/bin/deep.dll", "desktop.ini", "sub/Desktop.ini" })
            {
                var full = Path.Combine(root, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, "x");
            }
            var found = ImportClassifier.EnumerateFolder(root).Select(f => f.Path!.Value).OrderBy(p => p, StringComparer.Ordinal).ToArray();
            Assert.Equal(["RDPs/x.rdp", "a.cmd"], found);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
