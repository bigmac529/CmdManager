using CmdManager.Core;
using CmdManager.Core.Contracts;
using CmdManager.Core.Sync;

namespace CmdManager.Api.Tests;

/// <summary>End-to-end: FolderImporter + LibrarySyncService (Core) against the real API.</summary>
public sealed class SyncTests(ApiFactory factory) : IClassFixture<ApiFactory>, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cmdlib-tests-" + Guid.NewGuid().ToString("N"));

    private string Dir(string name)
    {
        var d = Path.Combine(_root, name);
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Write(string folder, string rel, string text) => Write(folder, rel, TextContent.ToBytes(text));

    private static void Write(string folder, string rel, byte[] bytes)
    {
        var full = Path.Combine(folder, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }

    private static string Read(string folder, string rel) => File.ReadAllText(Path.Combine(folder, rel.Replace('/', Path.DirectorySeparatorChar)));

    private static bool Exists(string folder, string rel) => File.Exists(Path.Combine(folder, rel.Replace('/', Path.DirectorySeparatorChar)));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Import_folder_then_pull_reproduces_it_byte_for_byte()
    {
        var api = await factory.RegisteredClientAsync();
        var src = Dir("CMDs");
        Write(src, "g.cmd", "start \"\" \"https://google.com/search?q=%*\"\r\nexit\r\n");
        Write(src, "New.csx", "\uFEFF#r \"CSXScripts/CSXScripts.dll\"\r\nusing CSXScripts;\r\nNew.Main(Env.ScriptArgs.ToArray());");
        Write(src, "RDPs/Work PC.rdp", [0xFF, 0xFE, 0x66, 0x00, 0x75, 0x00]);
        Write(src, "CSXScripts/CSXScripts.dll", [0x4D, 0x5A, 0x90, 0x00, 0x03]);
        Write(src, "CSXScripts/Settings.json", "{ \"Paths\": {} }");
        Write(src, "GitLatest.exe", [0x4D, 0x5A, 0x00, 0x01]);
        Write(src, "ansi.cmd", [0x65, 0x63, 0x68, 0x6F, 0x20, 0xE9]); // "echo é" in Windows-1252
        Write(src, "bin/Debug/skip.dll", [1]);
        Write(src, "obj/skip.txt", "x");
        Write(src, ".git/HEAD", "ref: x");
        Write(src, "desktop.ini", "[.ShellClassInfo]");

        var result = await new FolderImporter(api).ImportAsync(src);
        Assert.Empty(result.Errors);
        Assert.Equal(7, result.Created);

        var commands = await api.ListCommandsAsync();
        var assets = await api.ListAssetsAsync();
        Assert.Equal(["CSXScripts/Settings.json", "GitLatest.exe", "New.csx", "RDPs/Work PC.rdp", "ansi.cmd", "g.cmd"],
            commands.Select(c => c.RelativePath).OrderBy(p => p, StringComparer.Ordinal).ToArray());
        Assert.Equal("CSXScripts/CSXScripts.dll", Assert.Single(assets).RelativePath);
        Assert.True(commands.Single(c => c.Name == "ansi.cmd").IsBinary);

        var dest = Dir("cmds");
        var sync = new LibrarySyncService(api, Path.Combine(_root, "sync-state.json"));
        var pull = await sync.PullAsync(dest);
        Assert.Equal(7, pull.Downloaded);
        Assert.Empty(pull.Errors);

        foreach (var rel in new[] { "g.cmd", "New.csx", "RDPs/Work PC.rdp", "CSXScripts/CSXScripts.dll", "CSXScripts/Settings.json", "GitLatest.exe", "ansi.cmd" })
            Assert.Equal(File.ReadAllBytes(Path.Combine(src, rel)), File.ReadAllBytes(Path.Combine(dest, rel)));
        Assert.False(Exists(dest, "bin/Debug/skip.dll"));

        // Second pull: nothing to do
        var again = await sync.PullAsync(dest);
        Assert.Equal(0, again.Downloaded);
        Assert.Equal(7, again.InSync);
    }

    [Fact]
    public async Task Pull_mirrors_server_changes_and_deletions_but_never_deletes_untracked_files()
    {
        var api = await factory.RegisteredClientAsync();
        var a = await api.CreateCommandAsync(new CommandUpsertRequest("a.cmd", "v1"));
        var b = await api.CreateCommandAsync(new CommandUpsertRequest("sub/b.cmd", "b"));
        var dest = Dir("cmds");
        Write(dest, "mine.cmd", "my own file");
        var sync = new LibrarySyncService(api, Path.Combine(_root, "state.json"));
        await sync.PullAsync(dest);

        await api.UpdateCommandAsync(a.Id, new CommandUpsertRequest("a.cmd", "v2"));
        await api.DeleteCommandAsync(b.Id);
        await api.CreateCommandAsync(new CommandUpsertRequest("c.cmd", "c"));

        var pull = await sync.PullAsync(dest);
        Assert.Equal(2, pull.Downloaded);
        Assert.Equal(1, pull.DeletedLocal);
        Assert.Equal("v2", Read(dest, "a.cmd"));
        Assert.False(Exists(dest, "sub/b.cmd"));
        Assert.False(Directory.Exists(Path.Combine(dest, "sub")));
        Assert.Equal("c", Read(dest, "c.cmd"));
        Assert.True(Exists(dest, "mine.cmd"));
        Assert.Equal("mine.cmd", Assert.Single(pull.LocalOnly).RelativePath);
    }

    [Fact]
    public async Task Pull_leaves_local_edits_alone_and_reports_them_pending()
    {
        var api = await factory.RegisteredClientAsync();
        await api.CreateCommandAsync(new CommandUpsertRequest("a.cmd", "server"));
        var dest = Dir("cmds");
        var sync = new LibrarySyncService(api, Path.Combine(_root, "state.json"));
        await sync.PullAsync(dest);

        Write(dest, "a.cmd", "edited in notepad");
        Assert.Single(sync.DetectLocalChanges(dest));

        var pull = await sync.PullAsync(dest);
        Assert.Equal(0, pull.Downloaded);
        Assert.Equal("edited in notepad", Read(dest, "a.cmd"));
        Assert.Equal(SyncOp.Upload, Assert.Single(pull.PendingLocal).Op);
    }

    [Fact]
    public async Task Push_uploads_edits_and_additions_and_deletes_remote_copies_of_local_deletions()
    {
        var api = await factory.RegisteredClientAsync();
        var a = await api.CreateCommandAsync(new CommandUpsertRequest("a.cmd", "v1", Description: "keep me"));
        await api.CreateCommandAsync(new CommandUpsertRequest("gone.cmd", "bye"));
        await api.UploadAssetAsync("tools/t.dll", [1, 2, 3]);
        var dest = Dir("cmds");
        var sync = new LibrarySyncService(api, Path.Combine(_root, "state.json"));
        await sync.PullAsync(dest);

        Write(dest, "a.cmd", "v2 from notepad");
        Write(dest, "tools/t.dll", [4, 5, 6, 7]);
        Write(dest, "new.csx", "Console.WriteLine(Args.Count);");
        Write(dest, "blob.bin", [0, 1, 2, 0]);
        File.Delete(Path.Combine(dest, "gone.cmd"));

        Assert.Equal(5, sync.DetectLocalChanges(dest).Count);
        var plan = await sync.PlanAsync(dest);
        Assert.Equal(4, plan.Uploads.Count());
        Assert.Equal("gone.cmd", Assert.Single(plan.RemoteDeletes).RelativePath);
        Assert.Empty(plan.Conflicts);

        var push = await sync.PushAsync(plan);
        Assert.Empty(push.Errors);
        Assert.Empty(push.NewConflicts);
        Assert.Equal(4, push.Uploaded);
        Assert.Equal(1, push.DeletedRemote);

        var cmd = await api.GetCommandAsync(a.Id);
        Assert.Equal("v2 from notepad", cmd.TextContent);
        Assert.Equal("keep me", cmd.Description); // content-only upload keeps metadata
        var manifest = await api.GetManifestAsync();
        Assert.Equal(["a.cmd", "blob.bin", "new.csx", "tools/t.dll"], manifest.Items.Select(i => i.RelativePath).OrderBy(p => p, StringComparer.Ordinal).ToArray());
        Assert.Equal(LibraryItemType.Asset, manifest.Items.Single(i => i.RelativePath == "blob.bin").Type);

        Assert.Empty(sync.DetectLocalChanges(dest));
        var after = await sync.PlanAsync(dest);
        Assert.All(after.Items, i => Assert.Equal(SyncOp.NoOp, i.Op));
    }

    [Fact]
    public async Task Conflicts_are_detected_and_resolved_keep_local_server_both()
    {
        var api = await factory.RegisteredClientAsync();
        var one = await api.CreateCommandAsync(new CommandUpsertRequest("one.cmd", "base"));
        var two = await api.CreateCommandAsync(new CommandUpsertRequest("two.cmd", "base"));
        var three = await api.CreateCommandAsync(new CommandUpsertRequest("three.cmd", "base"));
        var dest = Dir("cmds");
        var sync = new LibrarySyncService(api, Path.Combine(_root, "state.json"));
        await sync.PullAsync(dest);

        foreach (var (c, name) in new[] { (one, "one"), (two, "two"), (three, "three") })
        {
            await api.UpdateCommandAsync(c.Id, new CommandUpsertRequest($"{name}.cmd", "server edit"));
            Write(dest, $"{name}.cmd", "local edit");
        }

        var plan = await sync.PlanAsync(dest);
        Assert.Equal(3, plan.Conflicts.Count());
        Assert.All(plan.Conflicts, c => Assert.Equal("edited locally and in the library", c.Reason));

        var push = await sync.PushAsync(plan, new Dictionary<string, ConflictResolution>
        {
            ["one.cmd"] = ConflictResolution.KeepLocal,
            ["two.cmd"] = ConflictResolution.KeepServer,
            ["three.cmd"] = ConflictResolution.KeepBoth
        });
        Assert.Empty(push.Errors);

        Assert.Equal("local edit", (await api.GetCommandAsync(one.Id)).TextContent);
        Assert.Equal("local edit", Read(dest, "one.cmd"));
        Assert.Equal("server edit", (await api.GetCommandAsync(two.Id)).TextContent);
        Assert.Equal("server edit", Read(dest, "two.cmd"));
        Assert.Equal("server edit", Read(dest, "three.cmd"));
        Assert.Equal("local edit", Read(dest, "three.conflict.cmd"));
        var copy = (await api.ListCommandsAsync()).Single(c => c.RelativePath == "three.conflict.cmd");
        Assert.Equal("local edit", (await api.GetCommandAsync(copy.Id)).TextContent);

        Assert.All((await sync.PlanAsync(dest)).Items, i => Assert.Equal(SyncOp.NoOp, i.Op));
    }

    [Fact]
    public async Task Push_turns_a_server_change_after_planning_into_a_conflict_instead_of_overwriting()
    {
        var api = await factory.RegisteredClientAsync();
        var c = await api.CreateCommandAsync(new CommandUpsertRequest("race.cmd", "base"));
        var dest = Dir("cmds");
        var sync = new LibrarySyncService(api, Path.Combine(_root, "state.json"));
        await sync.PullAsync(dest);

        Write(dest, "race.cmd", "local");
        var plan = await sync.PlanAsync(dest);                 // says: upload
        Assert.Equal(SyncOp.Upload, Assert.Single(plan.Items).Op);
        await api.UpdateCommandAsync(c.Id, new CommandUpsertRequest("race.cmd", "someone else")); // server moves on

        var push = await sync.PushAsync(plan);
        Assert.Equal(["race.cmd"], push.NewConflicts);
        Assert.Equal("someone else", (await api.GetCommandAsync(c.Id)).TextContent);
        Assert.Equal("local", Read(dest, "race.cmd"));
    }

    [Fact]
    public async Task WriteLocal_and_RemoveLocal_keep_the_folder_in_step_with_app_edits()
    {
        var api = await factory.RegisteredClientAsync();
        var dest = Dir("cmds");
        var sync = new LibrarySyncService(api, Path.Combine(_root, "state.json"));
        var c = await api.CreateCommandAsync(new CommandUpsertRequest("now.cmd", "@echo now"));
        var path = await sync.WriteLocalAsync(dest, new ManifestEntry(LibraryItemType.Command, c.Id, c.RelativePath, c.Sha256, c.Size, c.UpdatedUtc));
        Assert.Equal("@echo now", File.ReadAllText(path));
        Assert.Empty(sync.DetectLocalChanges(dest));

        Assert.True(sync.RemoveLocal(dest, "now.cmd"));
        Assert.False(File.Exists(path));
        Assert.False(sync.RemoveLocal(dest, "never-synced.cmd"));
    }
}
