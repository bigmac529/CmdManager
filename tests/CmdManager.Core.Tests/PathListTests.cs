using CmdManager.Core;

namespace CmdManager.Core.Tests;

public class PathListTests
{
    private const string Dir = @"C:\Users\m\AppData\Local\CmdManager\cmds";

    [Fact]
    public void Add_appends_when_missing()
    {
        Assert.Equal(@"C:\Tools;" + Dir, PathList.Add(@"C:\Tools", Dir));
        Assert.Equal(Dir, PathList.Add(null, Dir));
        Assert.Equal(Dir, PathList.Add("", Dir));
    }

    [Theory]
    [InlineData(@"C:\Users\m\AppData\Local\CmdManager\cmds")]
    [InlineData(@"c:\users\M\appdata\local\cmdmanager\CMDS")]
    [InlineData(@"C:\Users\m\AppData\Local\CmdManager\cmds\")]
    [InlineData(@"C:\Users\m\AppData\Local\CmdManager\cmds\\")]
    [InlineData(@"""C:\Users\m\AppData\Local\CmdManager\cmds""")]
    [InlineData(@"  C:\Users\m\AppData\Local\CmdManager\cmds  ")]
    [InlineData("C:/Users/m/AppData/Local/CmdManager/cmds")]
    public void Add_is_noop_when_present_in_any_spelling(string existing)
    {
        var path = @"C:\Tools;" + existing + @";C:\Other";
        Assert.Same(path, PathList.Add(path, Dir));
        Assert.True(PathList.Contains(path, Dir));
    }

    [Fact]
    public void Add_trailing_backslash_input_is_normalized()
    {
        Assert.Equal(@"C:\A;" + Dir, PathList.Add(@"C:\A", Dir + @"\"));
    }

    [Fact]
    public void Remove_removes_all_matching_entries_and_keeps_others_verbatim()
    {
        var path = @"%USERPROFILE%\bin;" + Dir + @"\;C:\Tools;" + Dir.ToUpperInvariant();
        Assert.Equal(@"%USERPROFILE%\bin;C:\Tools", PathList.Remove(path, Dir));
    }

    [Fact]
    public void Remove_returns_original_when_absent()
    {
        const string path = @"C:\A;;C:\B;";
        Assert.Same(path, PathList.Remove(path, Dir));
    }

    [Fact]
    public void Remove_drops_empty_entries_when_it_changes_something()
    {
        Assert.Equal(@"C:\A;C:\B", PathList.Remove(@"C:\A;;" + Dir + @";C:\B;", Dir));
    }

    [Fact]
    public void Dedupe_keeps_first_occurrence_case_insensitively()
    {
        Assert.Equal(@"C:\A;C:\B", PathList.Dedupe(@"C:\A;c:\a\;C:\B;;C:\b;C:\A"));
    }

    [Fact]
    public void Move_replaces_old_folder_with_new()
    {
        Assert.Equal(@"C:\A;D:\cmds", PathList.Move(@"C:\A;" + Dir, Dir, @"D:\cmds\"));
        Assert.Equal(@"C:\A;D:\cmds", PathList.Move(@"C:\A;D:\cmds", @"C:\old", @"D:\cmds"));
    }

    [Fact]
    public void Expand_function_matches_env_var_entries()
    {
        static string Expand(string s) => s.Replace("%LOCALAPPDATA%", @"C:\Users\m\AppData\Local", StringComparison.OrdinalIgnoreCase);
        var path = @"%LOCALAPPDATA%\CmdManager\cmds;C:\X";
        Assert.True(PathList.Contains(path, Dir, Expand));
        Assert.False(PathList.Contains(path, Dir));
        Assert.Equal(@"C:\X", PathList.Remove(path, Dir, Expand));
    }

    [Fact]
    public void Drive_root_keeps_its_backslash()
    {
        Assert.Equal(@"D:\", PathList.Normalize(@"D:\"));
        Assert.True(PathList.Contains(@"D:\", @"d:\\"));
    }

    [Fact]
    public void Rejects_entries_with_separator() => Assert.Throws<ArgumentException>(() => PathList.Add("", @"C:\a;b"));
}

public class PathExtTests
{
    private const string Machine = ".COM;.EXE;.BAT;.CMD;.VBS;.VBE;.JS;.JSE;.WSF;.WSH;.MSC;.CPL";

    [Fact]
    public void Adds_csx_on_top_of_machine_value_when_no_user_value()
    {
        Assert.Equal(Machine + ";.CSX", PathExt.WithExtension(Machine, null));
    }

    [Fact]
    public void Returns_null_when_already_effective()
    {
        Assert.Null(PathExt.WithExtension(Machine + ";.csx", null));
        Assert.Null(PathExt.WithExtension(Machine, ".COM;.EXE;.csx"));
    }

    [Fact]
    public void Merges_existing_user_extras_and_dedupes_case_insensitively()
    {
        var result = PathExt.WithExtension(Machine, ".py;.exe;.PY");
        Assert.Equal(Machine + ";.PY;.CSX", result);
    }

    [Fact]
    public void User_value_replaces_machine_so_effective_uses_it()
    {
        Assert.Equal(".EXE", PathExt.Effective(Machine, ".EXE"));
        Assert.Equal(Machine, PathExt.Effective(Machine, ""));
        Assert.Equal(PathExt.WindowsDefault, PathExt.Effective(null, null));
        Assert.False(PathExt.IsEffective(Machine + ";.CSX", ".EXE", ".csx")); // user value hides the machine .CSX
    }

    [Fact]
    public void Missing_machine_value_falls_back_to_windows_default()
    {
        Assert.Equal(PathExt.WindowsDefault + ";.CSX", PathExt.WithExtension(null, null));
    }

    [Fact]
    public void Normalizes_extensions()
    {
        Assert.Equal(".CSX", PathExt.NormalizeExtension("csx"));
        Assert.Equal(".CSX", PathExt.NormalizeExtension(" .Csx "));
        Assert.Equal([".A", ".B"], PathExt.Split(" .a ;; b;.A"));
    }

    [Fact]
    public void Remove_deletes_user_value_when_only_machine_entries_remain()
    {
        Assert.Null(PathExt.WithoutExtension(Machine, Machine + ";.CSX"));
        Assert.Null(PathExt.WithoutExtension(Machine, ".csx"));
        Assert.Null(PathExt.WithoutExtension(Machine, null));
    }

    [Fact]
    public void Remove_keeps_other_user_extras()
    {
        Assert.Equal(Machine + ";.PY", PathExt.WithoutExtension(Machine, Machine + ";.PY;.CSX"));
    }

    [Fact]
    public void Remove_is_noop_when_absent()
    {
        Assert.Equal(".PY;.EXE", PathExt.WithoutExtension(Machine, ".PY;.EXE"));
    }
}
