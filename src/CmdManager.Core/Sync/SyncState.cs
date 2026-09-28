using System.Text.Json;
using CmdManager.Core.Contracts;

namespace CmdManager.Core.Sync;

/// <summary>What the last sync left in the local folder for one file (the "base" of the three-way diff).</summary>
public sealed record SyncedFile(
    string RelativePath,
    string Sha256,
    DateTime ServerUpdatedUtc,
    long Size,
    DateTime LastWriteUtc,
    LibraryItemType Type = LibraryItemType.Command,
    int Id = 0);

/// <summary>
/// %LOCALAPPDATA%\CmdManager\sync-state.json: files the app itself wrote into the command folder, with their hash and
/// the server's updatedAt at that time. Only files listed here are ever deleted locally; anything else in the folder is
/// "local only". The state is discarded when the folder, server or user changes.
/// </summary>
public sealed class SyncState
{
    public const string DefaultFileName = "sync-state.json";

    public string? Folder { get; set; }
    public string? ServerUrl { get; set; }
    public int? UserId { get; set; }
    public DateTime? LastSyncUtc { get; set; }
    public Dictionary<string, SyncedFile> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Loads the state; returns an empty state bound to (folder, server, user) when it belongs to something else.</summary>
    public static SyncState Load(string statePath, string folder, string? serverUrl, int? userId)
    {
        SyncState? state = null;
        if (File.Exists(statePath))
        {
            try
            {
                state = JsonSerializer.Deserialize<SyncState>(File.ReadAllText(statePath));
            }
            catch (JsonException)
            {
                state = null;
            }
        }

        var full = NormalizeFolder(folder);
        if (state is null
            || !string.Equals(NormalizeFolder(state.Folder ?? ""), full, StringComparison.OrdinalIgnoreCase)
            || serverUrl is not null && state.ServerUrl is not null && !string.Equals(state.ServerUrl, serverUrl, StringComparison.OrdinalIgnoreCase)
            || userId is not null && state.UserId is not null && state.UserId != userId)
        {
            state = new SyncState();
        }

        state.Folder = full;
        state.ServerUrl = serverUrl ?? state.ServerUrl;
        state.UserId = userId ?? state.UserId;
        state.Files = new Dictionary<string, SyncedFile>(state.Files ?? new(), StringComparer.OrdinalIgnoreCase);
        return state;
    }

    public void Save(string statePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(statePath))!);
        var tmp = statePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, statePath, overwrite: true);
    }

    public static string NormalizeFolder(string folder) =>
        folder.Length == 0 ? "" : Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
