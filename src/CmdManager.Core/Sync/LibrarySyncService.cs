using CmdManager.Core.Contracts;
using CmdManager.Core.Http;

namespace CmdManager.Core.Sync;

public enum ConflictResolution
{
    /// <summary>Leave both sides as they are for now.</summary>
    Skip,

    /// <summary>Local version wins: upload it (or delete the DB copy if the local file was deleted).</summary>
    KeepLocal,

    /// <summary>Server version wins: download it (or delete the local file if the DB copy was removed).</summary>
    KeepServer,

    /// <summary>Rename the local file to name.conflict.ext and upload it as a new file, then download the server version.</summary>
    KeepBoth
}

/// <summary>Result of <see cref="LibrarySyncService.PlanAsync"/>: every path with its three-way decision.</summary>
public sealed record SyncPlan(string Folder, IReadOnlyList<SyncItem> Items, IReadOnlyDictionary<string, ManifestEntry> Server)
{
    public IEnumerable<SyncItem> Downloads => Items.Where(i => i.Op == SyncOp.Download);
    public IEnumerable<SyncItem> LocalDeletes => Items.Where(i => i.Op == SyncOp.DeleteLocal);
    public IEnumerable<SyncItem> Uploads => Items.Where(i => i.Op == SyncOp.Upload);
    public IEnumerable<SyncItem> RemoteDeletes => Items.Where(i => i.Op == SyncOp.DeleteRemote);
    public IEnumerable<SyncItem> Conflicts => Items.Where(i => i.Op == SyncOp.Conflict);
    public IEnumerable<SyncItem> LocalOnly => Items.Where(i => i.IsLocalOnly);

    /// <summary>Changes made outside the app that only "Sync local changes to DB" will send (uploads, remote deletes, conflicts).</summary>
    public IEnumerable<SyncItem> PendingLocal => Items.Where(i => i.Op is SyncOp.Upload or SyncOp.DeleteRemote or SyncOp.Conflict);
}

public sealed record PullResult(int Downloaded, int DeletedLocal, int InSync, IReadOnlyList<SyncItem> PendingLocal, IReadOnlyList<string> Errors)
{
    public IEnumerable<SyncItem> LocalOnly => PendingLocal.Where(i => i.IsLocalOnly);
}

public sealed record PushResult(
    int Uploaded,
    int DeletedRemote,
    int Downloaded,
    int DeletedLocal,
    IReadOnlyList<string> NewConflicts,
    IReadOnlyList<string> Errors);

/// <summary>
/// Two-way sync between the DB (source of truth) and the local command folder (the one on PATH).
/// <list type="bullet">
/// <item><see cref="PullAsync"/> mirrors the DB down: writes new/changed files, deletes files removed from the DB (only files
/// this app wrote, tracked in sync-state.json), and leaves local edits alone (they are reported as pending).</item>
/// <item><see cref="PushAsync"/> ("Sync local changes to DB") uploads local edits/additions and deletes DB copies of files
/// deleted locally, using the last-synced hash as an optimistic-concurrency check (409 → conflict).</item>
/// </list>
/// Text is written byte-exactly (UTF-8, no BOM added) so cmd.exe never sees a stray BOM.
/// </summary>
public sealed class LibrarySyncService(CmdManagerApiClient api, string statePath)
{
    private const string TempSuffix = ".cmdmanager.tmp";

    public string StatePath { get; } = statePath;

    // ---------------------------------------------------------------- planning

    public async Task<SyncPlan> PlanAsync(string folder, CancellationToken ct = default)
    {
        folder = SyncState.NormalizeFolder(folder);
        var manifest = await api.GetManifestAsync(ct);
        var state = LoadState(folder);
        return BuildPlan(folder, manifest, state);
    }

    /// <summary>Offline check for the folder watcher: files added, edited or deleted locally since the last sync.</summary>
    public IReadOnlyList<SyncItem> DetectLocalChanges(string folder)
    {
        folder = SyncState.NormalizeFolder(folder);
        if (!Directory.Exists(folder))
            return [];
        var state = LoadState(folder);
        var baseHashes = state.Files.ToDictionary(f => f.Key, f => f.Value.Sha256, StringComparer.OrdinalIgnoreCase);
        var local = ScanLocal(folder, state, baseHashes.Keys);
        // Pretend the server still equals the base: any difference is a local change.
        return ThreeWayDiff.Plan(local, baseHashes, baseHashes).Where(i => i.Op != SyncOp.NoOp).ToList();
    }

    // ---------------------------------------------------------------- pull (DB → folder)

    public async Task<PullResult> PullAsync(string folder, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        folder = SyncState.NormalizeFolder(folder);
        Directory.CreateDirectory(folder);
        var manifest = await api.GetManifestAsync(ct);
        var state = LoadState(folder);
        var plan = BuildPlan(folder, manifest, state);
        var errors = new List<string>();
        int downloaded = 0, deleted = 0, inSync = 0;

        foreach (var item in plan.Items)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                switch (item.Op)
                {
                    case SyncOp.NoOp when item.ServerExists:
                        Track(folder, state, plan.Server[item.RelativePath]);
                        inSync++;
                        break;
                    case SyncOp.NoOp:
                        state.Files.Remove(item.RelativePath);
                        break;
                    case SyncOp.Download:
                        progress?.Report($"Downloading {item.RelativePath}");
                        await DownloadAsync(folder, plan.Server[item.RelativePath], state, ct);
                        downloaded++;
                        break;
                    case SyncOp.DeleteLocal:
                        progress?.Report($"Deleting {item.RelativePath}");
                        DeleteLocal(folder, item.RelativePath, state);
                        deleted++;
                        break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ApiException { IsUnauthorized: false })
            {
                errors.Add($"{item.RelativePath}: {ex.Message}");
            }
        }

        SaveState(state);
        return new PullResult(downloaded, deleted, inSync, plan.PendingLocal.ToList(), errors);
    }

    // ---------------------------------------------------------------- push (folder → DB)

    /// <summary>
    /// Sends local changes from <paramref name="plan"/> (uploads + remote deletes) and applies the chosen conflict
    /// resolutions. Downloads/local deletes in the plan are applied too, so the folder ends up mirroring the DB.
    /// </summary>
    public async Task<PushResult> PushAsync(
        SyncPlan plan,
        IReadOnlyDictionary<string, ConflictResolution>? resolutions = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var folder = plan.Folder;
        var state = LoadState(folder);
        var errors = new List<string>();
        var conflicts = new List<string>();
        int uploaded = 0, deletedRemote = 0, downloaded = 0, deletedLocal = 0;
        var newFiles = new List<string>(); // relative paths to create on the server

        foreach (var item in plan.Items)
        {
            ct.ThrowIfCancellationRequested();
            plan.Server.TryGetValue(item.RelativePath, out var server);
            var op = item.Op;
            var force = false;
            if (op == SyncOp.Conflict)
            {
                var choice = resolutions?.GetValueOrDefault(item.RelativePath, ConflictResolution.Skip) ?? ConflictResolution.Skip;
                if (choice == ConflictResolution.KeepBoth && !(item.LocalExists && item.ServerExists))
                    choice = item.LocalExists ? ConflictResolution.KeepLocal : ConflictResolution.KeepServer;
                switch (choice)
                {
                    case ConflictResolution.Skip:
                        continue;
                    case ConflictResolution.KeepLocal:
                        op = item.LocalExists ? SyncOp.Upload : SyncOp.DeleteRemote;
                        force = true;
                        break;
                    case ConflictResolution.KeepServer:
                        op = item.ServerExists ? SyncOp.Download : SyncOp.DeleteLocal;
                        force = true;
                        break;
                    case ConflictResolution.KeepBoth:
                        try
                        {
                            var copy = MoveToConflictCopy(folder, item.RelativePath);
                            newFiles.Add(copy);
                            await DownloadAsync(folder, server!, state, ct);
                            downloaded++;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ApiException { IsUnauthorized: false })
                        {
                            errors.Add($"{item.RelativePath}: {ex.Message}");
                        }
                        continue;
                }
            }

            try
            {
                switch (op)
                {
                    case SyncOp.Upload when server is null:
                        newFiles.Add(item.RelativePath);
                        break;
                    case SyncOp.Upload:
                        progress?.Report($"Uploading {item.RelativePath}");
                        if (await UploadExistingAsync(folder, item, server, force ? null : item.BaseSha256, state, ct))
                            uploaded++;
                        else
                            conflicts.Add(item.RelativePath);
                        break;
                    case SyncOp.DeleteRemote when server is not null:
                        progress?.Report($"Deleting {item.RelativePath} from the library");
                        try
                        {
                            var expected = force ? null : item.BaseSha256;
                            if (server.Type == LibraryItemType.Command)
                                await api.DeleteCommandAsync(server.Id, expected, ct);
                            else
                                await api.DeleteAssetAsync(server.Id, expected, ct);
                            state.Files.Remove(item.RelativePath);
                            deletedRemote++;
                        }
                        catch (ApiException ex) when (ex.IsConflict)
                        {
                            conflicts.Add(item.RelativePath);
                        }
                        catch (ApiException ex) when (ex.IsNotFound)
                        {
                            state.Files.Remove(item.RelativePath);
                        }
                        break;
                    case SyncOp.Download when server is not null:
                        await DownloadAsync(folder, server, state, ct);
                        downloaded++;
                        break;
                    case SyncOp.DeleteLocal:
                        if (force || item.BaseSha256 is not null)
                        {
                            DeleteLocal(folder, item.RelativePath, state);
                            deletedLocal++;
                        }
                        break;
                    case SyncOp.NoOp when server is not null:
                        Track(folder, state, server);
                        break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ApiException { IsUnauthorized: false })
            {
                errors.Add($"{item.RelativePath}: {ex.Message}");
            }
        }

        if (newFiles.Count > 0)
        {
            progress?.Report($"Uploading {newFiles.Count} new file(s)");
            var (created, skipped, importErrors) = await CreateNewAsync(folder, newFiles, state, ct);
            uploaded += created;
            conflicts.AddRange(skipped);
            errors.AddRange(importErrors);
        }

        SaveState(state);
        return new PushResult(uploaded, deletedRemote, downloaded, deletedLocal, conflicts, errors);
    }

    // ---------------------------------------------------------------- single-file helpers

    /// <summary>
    /// Writes one library entry into the folder right away (after saving in the app, or before "Run") and records it
    /// in the sync state. Returns the full local path.
    /// </summary>
    public async Task<string> WriteLocalAsync(string folder, ManifestEntry entry, CancellationToken ct = default)
    {
        folder = SyncState.NormalizeFolder(folder);
        var full = FullPath(folder, entry.RelativePath);
        var state = LoadState(folder);
        if (!(File.Exists(full) && ContentHash.Equal(await ContentHash.Sha256HexOfFileAsync(full, ct), entry.Sha256)))
            await DownloadAsync(folder, entry, state, ct);
        else
            Track(folder, state, entry);
        SaveState(state);
        return full;
    }

    /// <summary>Removes a local file after it was deleted/renamed in the app (only if this app wrote it and it is unchanged).</summary>
    public bool RemoveLocal(string folder, string relativePath)
    {
        folder = SyncState.NormalizeFolder(folder);
        var state = LoadState(folder);
        if (!state.Files.TryGetValue(relativePath, out var synced))
            return false;
        var full = FullPath(folder, relativePath);
        if (File.Exists(full) && !ContentHash.Equal(HashFile(full), synced.Sha256))
            return false; // edited outside the app: keep it, it shows up as a pending local change
        DeleteLocal(folder, relativePath, state);
        SaveState(state);
        return true;
    }

    public static string FullPath(string folder, string relativePath)
    {
        var lp = LibraryPath.Parse(relativePath); // re-validates: no rooted paths / ".."
        var full = Path.GetFullPath(Path.Combine(folder, lp.Value.Replace('/', Path.DirectorySeparatorChar)));
        var root = SyncState.NormalizeFolder(folder) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException($"Path escapes the command folder: {relativePath}");
        return full;
    }

    /// <summary>"dir/name.ext" → "dir/name.conflict.ext" (then name.conflict2.ext, ... if taken).</summary>
    public static string ConflictCopyPath(string relativePath, Func<string, bool> exists)
    {
        var lp = LibraryPath.Parse(relativePath);
        var ext = Path.GetExtension(lp.Name);
        var stem = Path.GetFileNameWithoutExtension(lp.Name);
        for (var n = 1; ; n++)
        {
            var name = $"{stem}.conflict{(n == 1 ? "" : n.ToString())}{ext}";
            var candidate = LibraryPath.Combine(lp.Folder, name).Value;
            if (!exists(candidate))
                return candidate;
        }
    }

    // ---------------------------------------------------------------- internals

    private SyncState LoadState(string folder) => SyncState.Load(StatePath, folder, api.ServerUrl.ToString(), api.Session?.User.Id);

    private void SaveState(SyncState state)
    {
        state.LastSyncUtc = DateTime.UtcNow;
        state.Save(StatePath);
    }

    private static SyncPlan BuildPlan(string folder, ManifestDto manifest, SyncState state)
    {
        var server = new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in manifest.Items)
            server[e.RelativePath] = e;
        var serverHashes = server.ToDictionary(e => e.Key, e => e.Value.Sha256, StringComparer.OrdinalIgnoreCase);
        var baseHashes = state.Files.ToDictionary(f => f.Key, f => f.Value.Sha256, StringComparer.OrdinalIgnoreCase);
        var local = Directory.Exists(folder)
            ? ScanLocal(folder, state, serverHashes.Keys.Concat(baseHashes.Keys))
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return new SyncPlan(folder, ThreeWayDiff.Plan(local, baseHashes, serverHashes), server);
    }

    /// <summary>Hashes of files in the folder: everything the importer would pick up, plus any known path (even under bin/ etc.).</summary>
    private static Dictionary<string, string> ScanLocal(string folder, SyncState state, IEnumerable<string> knownPaths)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (full, path, _) in ImportClassifier.EnumerateFolder(folder))
        {
            if (path is null || full.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase))
                continue;
            result[path.Value] = CachedHash(full, path.Value, state);
        }

        foreach (var rel in knownPaths)
        {
            if (result.ContainsKey(rel))
                continue;
            var full = FullPath(folder, rel);
            if (File.Exists(full))
                result[rel] = CachedHash(full, rel, state);
        }

        return result;
    }

    private static string CachedHash(string full, string rel, SyncState state)
    {
        var info = new FileInfo(full);
        if (state.Files.TryGetValue(rel, out var s) && s.Size == info.Length && s.LastWriteUtc == info.LastWriteTimeUtc)
            return s.Sha256;
        return HashFile(full);
    }

    private static string HashFile(string full)
    {
        using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return ContentHash.Sha256Hex(fs);
    }

    private async Task DownloadAsync(string folder, ManifestEntry entry, SyncState state, CancellationToken ct)
    {
        var bytes = await api.GetContentAsync(entry, ct);
        var hash = ContentHash.Sha256Hex(bytes);
        if (!ContentHash.Equal(hash, entry.Sha256))
            throw new InvalidDataException("content changed on the server during sync; sync again.");

        var full = FullPath(folder, entry.RelativePath);
        var dir = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, "." + Path.GetFileName(full) + TempSuffix);
        await File.WriteAllBytesAsync(tmp, bytes, ct);
        if (File.Exists(full))
            File.SetAttributes(full, FileAttributes.Normal);
        File.Move(tmp, full, overwrite: true);
        Track(folder, state, entry);
    }

    private static void Track(string folder, SyncState state, ManifestEntry entry)
    {
        var info = new FileInfo(FullPath(folder, entry.RelativePath));
        state.Files[entry.RelativePath] = new SyncedFile(entry.RelativePath, entry.Sha256.ToLowerInvariant(), entry.UpdatedUtc,
            info.Exists ? info.Length : entry.Size, info.Exists ? info.LastWriteTimeUtc : default, entry.Type, entry.Id);
    }

    private static void DeleteLocal(string folder, string relativePath, SyncState state)
    {
        var full = FullPath(folder, relativePath);
        if (File.Exists(full))
        {
            File.SetAttributes(full, FileAttributes.Normal);
            File.Delete(full);
        }
        state.Files.Remove(relativePath);
        DeleteEmptyParents(folder, Path.GetDirectoryName(full));
    }

    private static string MoveToConflictCopy(string folder, string relativePath)
    {
        var copy = ConflictCopyPath(relativePath, p => File.Exists(FullPath(folder, p)));
        File.Move(FullPath(folder, relativePath), FullPath(folder, copy));
        return copy;
    }

    /// <summary>Uploads new content for an existing item. False on 409 (changed on the server since the last sync).</summary>
    private async Task<bool> UploadExistingAsync(string folder, SyncItem item, ManifestEntry server, string? expectedSha, SyncState state, CancellationToken ct)
    {
        var full = FullPath(folder, item.RelativePath);
        var bytes = await File.ReadAllBytesAsync(full, ct);
        try
        {
            ManifestEntry stored;
            if (server.Type == LibraryItemType.Command)
            {
                var c = await api.ReplaceCommandContentAsync(server.Id, bytes, expectedSha, ct);
                stored = new ManifestEntry(LibraryItemType.Command, c.Id, c.RelativePath, c.Sha256, c.Size, c.UpdatedUtc);
            }
            else
            {
                var a = await api.ReplaceAssetContentAsync(server.Id, bytes, expectedSha, ct);
                stored = new ManifestEntry(LibraryItemType.Asset, a.Id, a.RelativePath, a.Sha256, a.Size, a.UpdatedUtc);
            }
            Track(folder, state, stored);
            return true;
        }
        catch (ApiException ex) when (ex.IsConflict)
        {
            return false;
        }
    }

    private async Task<(int Created, List<string> Skipped, List<string> Errors)> CreateNewAsync(
        string folder, List<string> paths, SyncState state, CancellationToken ct)
    {
        int created = 0;
        var skipped = new List<string>();
        var errors = new List<string>();
        var batch = new List<ImportItem>();
        long batchBytes = 0;

        async Task FlushAsync()
        {
            if (batch.Count == 0)
                return;
            var result = await api.ImportAsync(new ImportRequest(batch.ToList(), Overwrite: false), ct);
            foreach (var r in result.Items ?? [])
            {
                switch (r.Status)
                {
                    case ImportItemStatus.Created or ImportItemStatus.Updated or ImportItemStatus.Unchanged when r.Id is not null && r.Sha256 is not null:
                        if (r.Status != ImportItemStatus.Unchanged)
                            created++;
                        var full = FullPath(folder, r.RelativePath);
                        Track(folder, state, new ManifestEntry(r.Type ?? LibraryItemType.Command, r.Id.Value, r.RelativePath, r.Sha256,
                            new FileInfo(full).Length, r.UpdatedUtc ?? DateTime.UtcNow));
                        break;
                    case ImportItemStatus.Skipped:
                        skipped.Add(r.RelativePath);
                        break;
                    case ImportItemStatus.Error:
                        errors.Add($"{r.RelativePath}: {r.Error}");
                        break;
                }
            }
            batch.Clear();
            batchBytes = 0;
        }

        foreach (var rel in paths)
        {
            var bytes = await File.ReadAllBytesAsync(FullPath(folder, rel), ct);
            var lp = LibraryPath.Parse(rel);
            var type = ImportClassifier.Classify(lp, bytes);
            var item = type == LibraryItemType.Command && TextContent.TryDecode(bytes, out var text)
                ? new ImportItem(lp.Value, Text: text, Type: type)
                : new ImportItem(lp.Value, Content: bytes, Type: type);
            if (batch.Count > 0 && batchBytes + bytes.Length > 8 * 1024 * 1024)
                await FlushAsync();
            batch.Add(item);
            batchBytes += bytes.Length;
        }

        await FlushAsync();
        return (created, skipped, errors);
    }

    private static void DeleteEmptyParents(string root, string? dir)
    {
        var rootFull = SyncState.NormalizeFolder(root);
        while (!string.IsNullOrEmpty(dir)
               && !string.Equals(SyncState.NormalizeFolder(dir), rootFull, StringComparison.OrdinalIgnoreCase)
               && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
        {
            Directory.Delete(dir);
            dir = Path.GetDirectoryName(dir);
        }
    }
}
