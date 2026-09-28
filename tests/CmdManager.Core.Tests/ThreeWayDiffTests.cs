using CmdManager.Core.Sync;

namespace CmdManager.Core.Tests;

public class ThreeWayDiffTests
{
    private const string A = "aaaa", B = "bbbb", C = "cccc";

    [Theory]
    // local, base, server → op
    // --- nothing to do
    [InlineData(null, null, null, SyncOp.NoOp)]
    [InlineData(A, A, A, SyncOp.NoOp)]
    [InlineData(A, null, A, SyncOp.NoOp)]      // identical file already there (first sync)
    [InlineData(B, A, B, SyncOp.NoOp)]         // same edit made on both sides
    [InlineData(null, A, null, SyncOp.NoOp)]   // deleted on both sides
    // --- never synced
    [InlineData(null, null, A, SyncOp.Download)]    // new in DB
    [InlineData(A, null, null, SyncOp.Upload)]      // local only
    [InlineData(A, null, B, SyncOp.Conflict)]       // added on both sides, different
    // --- local unchanged → server wins
    [InlineData(A, A, B, SyncOp.Download)]          // changed in DB
    [InlineData(null, A, A, SyncOp.DeleteRemote)]   // deleted locally, DB unchanged
    [InlineData(A, A, null, SyncOp.DeleteLocal)]    // removed from DB
    // --- server unchanged → local wins
    [InlineData(B, A, A, SyncOp.Upload)]            // edited locally
    // --- both changed
    [InlineData(B, A, C, SyncOp.Conflict)]          // edit / edit
    [InlineData(null, A, B, SyncOp.Conflict)]       // local delete / server edit
    [InlineData(B, A, null, SyncOp.Conflict)]       // local edit / server delete
    public void Decide(string? local, string? @base, string? server, SyncOp expected) =>
        Assert.Equal(expected, ThreeWayDiff.Decide(local, @base, server));

    [Fact]
    public void Hashes_compare_case_insensitively()
    {
        Assert.Equal(SyncOp.NoOp, ThreeWayDiff.Decide("ABCD", "abcd", "AbCd"));
        Assert.Equal(SyncOp.Upload, ThreeWayDiff.Decide("ffff", "ABCD", "abcd"));
    }

    [Fact]
    public void Missing_local_but_tracked_and_server_changed_downloads_nothing_silently()
    {
        // deleted locally while the server changed: user must decide
        Assert.Equal(SyncOp.Conflict, ThreeWayDiff.Decide(null, A, B));
    }

    [Fact]
    public void Plan_covers_union_of_paths_case_insensitively_and_sorted()
    {
        var local = new Dictionary<string, string> { ["b.cmd"] = B, ["new.cmd"] = C, ["same.cmd"] = A };
        var @base = new Dictionary<string, string> { ["B.CMD"] = A, ["gone.cmd"] = A, ["same.cmd"] = A };
        var server = new Dictionary<string, string> { ["b.cmd"] = A, ["gone.cmd"] = A, ["same.cmd"] = A, ["srv.cmd"] = B };

        var plan = ThreeWayDiff.Plan(local, @base, server);
        Assert.Equal(["b.cmd", "gone.cmd", "new.cmd", "same.cmd", "srv.cmd"], plan.Select(p => p.RelativePath).ToArray());
        Assert.Equal(SyncOp.Upload, plan[0].Op);        // b.cmd edited locally
        Assert.Equal(SyncOp.DeleteRemote, plan[1].Op);  // gone.cmd deleted locally
        Assert.Equal(SyncOp.Upload, plan[2].Op);        // new.cmd local only
        Assert.True(plan[2].IsLocalOnly);
        Assert.False(plan[0].IsLocalOnly);
        Assert.Equal(SyncOp.NoOp, plan[3].Op);
        Assert.Equal(SyncOp.Download, plan[4].Op);      // srv.cmd new in DB
    }

    [Theory]
    [InlineData(null, null, "a", "new in the library")]
    [InlineData("a", "a", "b", "changed in the library")]
    [InlineData("a", "a", null, "removed from the library")]
    [InlineData("b", "a", "a", "edited locally")]
    [InlineData("a", null, null, "new local file")]
    [InlineData(null, "a", "a", "deleted locally")]
    [InlineData("b", "a", "c", "edited locally and in the library")]
    [InlineData(null, "a", "c", "deleted locally but edited in the library")]
    [InlineData("b", "a", null, "edited locally but removed from the library")]
    [InlineData("b", null, "c", "added locally and in the library with different content")]
    public void Reasons_are_readable(string? l, string? b, string? s, string reason)
    {
        var item = new SyncItem("x", l, b, s, ThreeWayDiff.Decide(l, b, s));
        Assert.Equal(reason, item.Reason);
    }
}

public class ConflictCopyTests
{
    [Fact]
    public void Conflict_copy_name_inserts_marker_before_extension()
    {
        Assert.Equal("RDPs/work.conflict.rdp", LibrarySyncService.ConflictCopyPath("RDPs/work.rdp", _ => false));
        Assert.Equal("Makefile.conflict", LibrarySyncService.ConflictCopyPath("Makefile", _ => false));
    }

    [Fact]
    public void Conflict_copy_name_counts_up_when_taken()
    {
        var taken = new HashSet<string> { "g.conflict.cmd", "g.conflict2.cmd" };
        Assert.Equal("g.conflict3.cmd", LibrarySyncService.ConflictCopyPath("g.cmd", taken.Contains));
    }

    [Fact]
    public void FullPath_rejects_escaping_paths()
    {
        var root = Path.Combine(Path.GetTempPath(), "cmds");
        Assert.Throws<ArgumentException>(() => LibrarySyncService.FullPath(root, "../x.cmd"));
        Assert.StartsWith(root, LibrarySyncService.FullPath(root, "RDPs/a.rdp"));
    }
}
