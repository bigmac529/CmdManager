using CmdManager.Api.Data;
using CmdManager.Core;
using CmdManager.Core.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CmdManager.Api.Library;

/// <summary>Shared rules for commands and assets: per-user path uniqueness across both tables, size limits, mapping.</summary>
public sealed class LibraryService(CmdManagerDbContext db, IOptions<LibraryOptions> options, TimeProvider time)
{
    public long MaxFileBytes => options.Value.MaxFileBytes;

    public DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>True when another command or asset of this user already uses <paramref name="key"/>.</summary>
    public async Task<bool> PathTakenAsync(int userId, string key, int? exceptCommandId = null, int? exceptAssetId = null, CancellationToken ct = default) =>
        await db.Commands.AnyAsync(c => c.UserId == userId && c.PathKey == key && c.Id != (exceptCommandId ?? 0), ct) ||
        await db.Assets.AnyAsync(a => a.UserId == userId && a.PathKey == key && a.Id != (exceptAssetId ?? 0), ct);

    public string? CheckSize(long size) =>
        size > MaxFileBytes ? $"File is {size / 1048576.0:0.#} MB; the server limit is {MaxFileBytes / 1048576.0:0.#} MB per file." : null;

    /// <summary>Parses an If-Match / expectedSha256 value ("abc…", "\"abc…\"", W/"abc…"); null when absent or "*".</summary>
    public static string? ExpectedHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var v = value.Trim();
        if (v.StartsWith("W/", StringComparison.Ordinal))
            v = v[2..];
        v = v.Trim('"');
        return v == "*" ? null : v;
    }

    public static string? NormalizeTags(string[]? tags)
    {
        if (tags is null)
            return null;
        var clean = tags.Select(t => t.Trim().Replace(";", "").Replace(",", ""))
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var joined = string.Join(';', clean);
        if (joined.Length > 1000)
            throw new ArgumentException("Tags are too long (max 1000 characters in total).");
        return clean.Length == 0 ? null : joined;
    }

    public static string[] SplitTags(string? tags) =>
        string.IsNullOrEmpty(tags) ? [] : tags.Split(';', StringSplitOptions.RemoveEmptyEntries);

    public static string? CleanDescription(string? d)
    {
        d = d?.Trim();
        if (string.IsNullOrEmpty(d))
            return null;
        if (d.Length > 1000)
            throw new ArgumentException("Description is too long (max 1000 characters).");
        return d;
    }

    public static CommandSummaryDto ToSummary(Command c) =>
        new(c.Id, c.Name, c.Folder, c.RelativePath, c.Kind, c.Description, SplitTags(c.Tags), c.IsBinary, c.Size, c.Sha256, c.CreatedUtc, c.UpdatedUtc);

    public static CommandDto ToDto(Command c) =>
        new(c.Id, c.Name, c.Folder, c.RelativePath, c.Kind, c.Description, SplitTags(c.Tags), c.IsBinary, c.Size, c.Sha256, c.CreatedUtc, c.UpdatedUtc,
            c.IsBinary ? null : c.TextContent ?? "");

    public static AssetDto ToDto(Asset a) =>
        new(a.Id, a.Name, a.Folder, a.RelativePath, a.Description, a.Size, a.Sha256, a.CreatedUtc, a.UpdatedUtc);

    /// <summary>Bulk import (POST /api/library/import). One SaveChanges per request.</summary>
    public async Task<ImportResult> ImportAsync(int userId, ImportRequest request, CancellationToken ct)
    {
        int created = 0, updated = 0, unchanged = 0, skipped = 0;
        var errors = new List<ImportError>();
        var results = new List<ImportItemResult>();
        var saved = new List<(string Path, LibraryFile File, LibraryItemType Type, ImportItemStatus Status)>();
        var now = Now;
        void Fail(string path, string error)
        {
            errors.Add(new ImportError(path, error));
            results.Add(new ImportItemResult(path, ImportItemStatus.Error, Error: error));
        }

        // Load the metadata (no content) of everything this user has, keyed by path.
        var commands = await db.Commands.Where(c => c.UserId == userId)
            .Select(c => new { c.Id, c.PathKey, c.Sha256 }).ToDictionaryAsync(c => c.PathKey, ct);
        var assets = await db.Assets.Where(a => a.UserId == userId)
            .Select(a => new { a.Id, a.PathKey, a.Sha256 }).ToDictionaryAsync(a => a.PathKey, ct);
        var seen = new HashSet<string>();

        foreach (var item in request.Items ?? [])
        {
            if (!LibraryPath.TryParse(item.RelativePath, out var path, out var pathError))
            {
                Fail(item.RelativePath ?? "", pathError);
                continue;
            }

            if (!seen.Add(path.Key))
            {
                Fail(path.Value, "Duplicate path in this import.");
                continue;
            }

            if (item.Text is not null && item.Content is not null)
            {
                Fail(path.Value, "Provide either text or content, not both.");
                continue;
            }

            var bytes = item.Content ?? (item.Text is null ? [] : TextContent.ToBytes(item.Text));
            if (CheckSize(bytes.LongLength) is { } sizeError)
            {
                Fail(path.Value, sizeError);
                continue;
            }

            string? description;
            try
            {
                description = CleanDescription(item.Description);
            }
            catch (ArgumentException ex)
            {
                Fail(path.Value, ex.Message);
                continue;
            }

            var type = item.Type ?? ImportClassifier.Classify(path, bytes);
            var sha = ContentHash.Sha256Hex(bytes);
            commands.TryGetValue(path.Key, out var existingCmd);
            assets.TryGetValue(path.Key, out var existingAsset);

            if (type == LibraryItemType.Command && existingAsset is not null || type == LibraryItemType.Asset && existingCmd is not null)
            {
                Fail(path.Value, $"Already exists as {(existingCmd is not null ? "a command" : "an asset")}; delete it first to change its type.");
                continue;
            }

            var existingSha = existingCmd?.Sha256 ?? existingAsset?.Sha256;
            if (existingSha is not null)
            {
                if (ContentHash.Equal(existingSha, sha))
                {
                    unchanged++;
                    results.Add(new ImportItemResult(path.Value, ImportItemStatus.Unchanged,
                        existingCmd is not null ? LibraryItemType.Command : LibraryItemType.Asset, existingCmd?.Id ?? existingAsset!.Id, existingSha));
                    continue;
                }
                if (!request.Overwrite)
                {
                    skipped++;
                    results.Add(new ImportItemResult(path.Value, ImportItemStatus.Skipped,
                        existingCmd is not null ? LibraryItemType.Command : LibraryItemType.Asset, existingCmd?.Id ?? existingAsset!.Id, existingSha,
                        Error: "Already exists with different content."));
                    continue;
                }
            }

            if (type == LibraryItemType.Command)
            {
                Command cmd;
                if (existingCmd is not null)
                {
                    cmd = await db.Commands.SingleAsync(c => c.Id == existingCmd.Id, ct);
                    updated++;
                    saved.Add((path.Value, cmd, LibraryItemType.Command, ImportItemStatus.Updated));
                }
                else
                {
                    cmd = new Command { UserId = userId, CreatedUtc = now };
                    cmd.SetPath(path);
                    cmd.Kind = path.Kind;
                    db.Commands.Add(cmd);
                    created++;
                    saved.Add((path.Value, cmd, LibraryItemType.Command, ImportItemStatus.Created));
                }

                if (item.Text is not null)
                    cmd.SetText(item.Text);
                else if (TextContent.TryDecode(bytes, out var decoded) && cmd.Kind is not (CommandKind.Exe or CommandKind.Lnk))
                    cmd.SetText(decoded);
                else
                    cmd.SetBinary(bytes);
                cmd.Description = description ?? cmd.Description;
                cmd.UpdatedUtc = now;
            }
            else
            {
                Asset asset;
                if (existingAsset is not null)
                {
                    asset = await db.Assets.SingleAsync(a => a.Id == existingAsset.Id, ct);
                    updated++;
                    saved.Add((path.Value, asset, LibraryItemType.Asset, ImportItemStatus.Updated));
                }
                else
                {
                    asset = new Asset { UserId = userId, CreatedUtc = now };
                    asset.SetPath(path);
                    db.Assets.Add(asset);
                    created++;
                    saved.Add((path.Value, asset, LibraryItemType.Asset, ImportItemStatus.Created));
                }

                asset.SetContent(bytes);
                asset.Description = description ?? asset.Description;
                asset.UpdatedUtc = now;
            }
        }

        await db.SaveChangesAsync(ct);
        results.AddRange(saved.Select(x => new ImportItemResult(x.Path, x.Status, x.Type, x.File.Id, x.File.Sha256, x.File.UpdatedUtc)));
        return new ImportResult(created, updated, unchanged, skipped, errors, results);
    }
}
