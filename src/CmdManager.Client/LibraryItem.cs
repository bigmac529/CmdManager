using CmdManager.Core;
using CmdManager.Core.Contracts;

namespace CmdManager.Client;

/// <summary>Row in the main list: a command or an asset.</summary>
public sealed class LibraryItem
{
    public LibraryItemType Type { get; init; }
    public int Id { get; init; }
    public string RelativePath { get; init; } = "";
    public string Name { get; init; } = "";
    public string Folder { get; init; } = "";
    public CommandKind Kind { get; init; }
    public string? Description { get; init; }
    public string[] Tags { get; init; } = [];
    public bool IsBinary { get; init; }
    public long Size { get; init; }
    public string Sha256 { get; init; } = "";
    public DateTime UpdatedUtc { get; init; }

    public bool IsCommand => Type == LibraryItemType.Command;
    public bool IsEditable => IsCommand && !IsBinary;
    public string KindText => IsCommand ? Kind.ToString().ToLowerInvariant() + (IsBinary ? " (binary)" : "") : "asset";
    public string SizeText => Size < 1024 ? $"{Size} B" : Size < 1048576 ? $"{Size / 1024.0:0.#} KB" : $"{Size / 1048576.0:0.#} MB";
    public string UpdatedText => UpdatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string TagsText => string.Join(", ", Tags);

    public ManifestEntry ToManifestEntry() => new(Type, Id, RelativePath, Sha256, Size, UpdatedUtc);

    public static LibraryItem From(CommandSummaryDto c) => new()
    {
        Type = LibraryItemType.Command, Id = c.Id, RelativePath = c.RelativePath, Name = c.Name, Folder = c.Folder, Kind = c.Kind,
        Description = c.Description, Tags = c.Tags, IsBinary = c.IsBinary, Size = c.Size, Sha256 = c.Sha256, UpdatedUtc = c.UpdatedUtc
    };

    public static LibraryItem From(AssetDto a) => new()
    {
        Type = LibraryItemType.Asset, Id = a.Id, RelativePath = a.RelativePath, Name = a.Name, Folder = a.Folder, Kind = CommandKind.Other,
        Description = a.Description, IsBinary = true, Size = a.Size, Sha256 = a.Sha256, UpdatedUtc = a.UpdatedUtc
    };

    public bool Matches(string filter) =>
        RelativePath.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || (Description?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
        || Tags.Any(t => t.Contains(filter, StringComparison.OrdinalIgnoreCase));
}
