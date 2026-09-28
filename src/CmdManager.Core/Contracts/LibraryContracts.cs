namespace CmdManager.Core.Contracts;

/// <summary>A command without its content (list views).</summary>
public record CommandSummaryDto(
    int Id,
    string Name,
    string Folder,
    string RelativePath,
    CommandKind Kind,
    string? Description,
    string[] Tags,
    bool IsBinary,
    long Size,
    string Sha256,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);

/// <summary>A command with its text content (null for binary commands; download those via /content).</summary>
public sealed record CommandDto(
    int Id,
    string Name,
    string Folder,
    string RelativePath,
    CommandKind Kind,
    string? Description,
    string[] Tags,
    bool IsBinary,
    long Size,
    string Sha256,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    string? TextContent)
    : CommandSummaryDto(Id, Name, Folder, RelativePath, Kind, Description, Tags, IsBinary, Size, Sha256, CreatedUtc, UpdatedUtc);

/// <summary>
/// Create (POST /api/commands) or update (PUT /api/commands/{id}) a command.
/// RelativePath is "folder/name.ext" (e.g. "g.cmd", "RDPs/work.rdp"); changing it on update renames/moves.
/// Provide TextContent OR BinaryContent (base64 in JSON). On update, leaving both null keeps the content.
/// Kind is inferred from the extension when null.
/// ExpectedSha256 (update only, optional) makes the save fail with 409 when the stored content changed meanwhile.
/// </summary>
public sealed record CommandUpsertRequest(
    string RelativePath,
    string? TextContent = null,
    byte[]? BinaryContent = null,
    string? Description = null,
    string[]? Tags = null,
    CommandKind? Kind = null,
    string? ExpectedSha256 = null);

public sealed record AssetDto(
    int Id,
    string Name,
    string Folder,
    string RelativePath,
    string? Description,
    long Size,
    string Sha256,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);

/// <summary>PUT /api/assets/{id}: rename/move and/or change the description (content is replaced via PUT /api/assets/{id}/content).</summary>
public sealed record AssetUpdateRequest(string RelativePath, string? Description = null);

public enum LibraryItemType
{
    Command,
    Asset
}

public sealed record ManifestEntry(
    LibraryItemType Type,
    int Id,
    string RelativePath,
    string Sha256,
    long Size,
    DateTime UpdatedUtc);

/// <summary>GET /api/library/manifest: everything the user owns, for hash-based client sync.</summary>
public sealed record ManifestDto(DateTime GeneratedUtc, IReadOnlyList<ManifestEntry> Items);

/// <summary>One file of a bulk import. Text files: set Text; everything else: set Content (base64 in JSON).</summary>
public sealed record ImportItem(
    string RelativePath,
    string? Text = null,
    byte[]? Content = null,
    LibraryItemType? Type = null,
    string? Description = null);

/// <summary>POST /api/library/import. Overwrite=false leaves existing paths untouched (reported as skipped).</summary>
public sealed record ImportRequest(IReadOnlyList<ImportItem> Items, bool Overwrite = true);

public sealed record ImportResult(
    int Created,
    int Updated,
    int Unchanged,
    int Skipped,
    IReadOnlyList<ImportError> Errors,
    IReadOnlyList<ImportItemResult>? Items = null);

public enum ImportItemStatus
{
    Created,
    Updated,
    Unchanged,
    /// <summary>Path already existed with different content and Overwrite was false.</summary>
    Skipped,
    Error
}

/// <summary>Per-file outcome of an import; Id/Sha256/UpdatedUtc describe the stored item (for sync state).</summary>
public sealed record ImportItemResult(
    string RelativePath,
    ImportItemStatus Status,
    LibraryItemType? Type = null,
    int? Id = null,
    string? Sha256 = null,
    DateTime? UpdatedUtc = null,
    string? Error = null);

public sealed record ImportError(string RelativePath, string Error);
