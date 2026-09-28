namespace CmdManager.Core.Sync;

public enum SyncOp
{
    /// <summary>Local and server already agree (or both are gone).</summary>
    NoOp,

    /// <summary>Server has a new or changed file, local copy is unchanged since the last sync (or missing): write it locally.</summary>
    Download,

    /// <summary>File was removed from the DB and the local copy is unchanged since the last sync: delete it locally.</summary>
    DeleteLocal,

    /// <summary>File was edited locally (server unchanged) or added locally (not on server): upload it.</summary>
    Upload,

    /// <summary>File was deleted locally and the server copy is unchanged since the last sync: delete it from the DB.</summary>
    DeleteRemote,

    /// <summary>Changed on both sides since the last sync (including edit-vs-delete and add-vs-add with different content).</summary>
    Conflict
}

/// <summary>One path's three versions: local file, last-synced base, and server manifest (null hash = absent).</summary>
public sealed record SyncItem(string RelativePath, string? LocalSha256, string? BaseSha256, string? ServerSha256, SyncOp Op)
{
    public bool LocalExists => LocalSha256 is not null;
    public bool ServerExists => ServerSha256 is not null;
    public bool BaseExists => BaseSha256 is not null;

    /// <summary>Upload of a file the server has never had (a "local only" file).</summary>
    public bool IsLocalOnly => Op == SyncOp.Upload && !ServerExists && !BaseExists;

    /// <summary>Human-readable reason for dialogs / logs.</summary>
    public string Reason => Op switch
    {
        SyncOp.NoOp => "in sync",
        SyncOp.Download => LocalExists ? "changed in the library" : BaseExists ? "missing locally" : "new in the library",
        SyncOp.DeleteLocal => "removed from the library",
        SyncOp.Upload => ServerExists ? "edited locally" : BaseExists ? "edited locally (removed from library meanwhile)" : "new local file",
        SyncOp.DeleteRemote => "deleted locally",
        SyncOp.Conflict => (LocalExists, ServerExists, BaseExists) switch
        {
            (true, true, false) => "added locally and in the library with different content",
            (true, true, true) => "edited locally and in the library",
            (false, true, _) => "deleted locally but edited in the library",
            (true, false, _) => "edited locally but removed from the library",
            _ => "conflict"
        },
        _ => ""
    };
}

/// <summary>
/// Pure three-way comparison: local hash vs. last-synced (base) hash vs. server hash. Hash comparison is
/// case-insensitive; null means the file does not exist on that side.
/// </summary>
public static class ThreeWayDiff
{
    public static SyncOp Decide(string? local, string? @base, string? server)
    {
        if (Same(local, server))
            return SyncOp.NoOp;                             // identical, or gone on both sides

        if (@base is null)                                  // never synced this path
        {
            if (local is null) return SyncOp.Download;      // new on server
            if (server is null) return SyncOp.Upload;       // local only
            return SyncOp.Conflict;                         // both added, different content
        }

        if (Same(local, @base))                             // local untouched since last sync → server wins
            return server is null ? SyncOp.DeleteLocal : SyncOp.Download;

        if (Same(server, @base))                            // server untouched since last sync → local wins
            return local is null ? SyncOp.DeleteRemote : SyncOp.Upload;

        return SyncOp.Conflict;                             // both changed (edit/edit, edit/delete, delete/edit)
    }

    /// <summary>Builds the plan over the union of all paths known to any side, ordered by path.</summary>
    public static IReadOnlyList<SyncItem> Plan(
        IReadOnlyDictionary<string, string> local,
        IReadOnlyDictionary<string, string> @base,
        IReadOnlyDictionary<string, string> server)
    {
        var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        paths.UnionWith(local.Keys);
        paths.UnionWith(@base.Keys);
        paths.UnionWith(server.Keys);
        var result = new List<SyncItem>(paths.Count);
        foreach (var p in paths)
        {
            var l = Get(local, p);
            var b = Get(@base, p);
            var s = Get(server, p);
            result.Add(new SyncItem(p, l, b, s, Decide(l, b, s)));
        }
        return result;
    }

    private static string? Get(IReadOnlyDictionary<string, string> d, string key)
    {
        if (d.TryGetValue(key, out var v))
            return v;
        foreach (var (k, value) in d)
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                return value;
        return null;
    }

    private static bool Same(string? a, string? b) =>
        a is null ? b is null : b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
