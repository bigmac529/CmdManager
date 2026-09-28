using System.IO.Compression;
using CmdManager.Api.Auth;
using CmdManager.Api.Data;
using CmdManager.Core.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CmdManager.Api.Library;

public static class LibraryEndpoints
{
    public static RouteGroupBuilder MapLibraryEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/library").WithTags("Library").RequireAuthorization();
        g.MapGet("/manifest", Manifest);
        g.MapPost("/import", Import);
        g.MapGet("/export", Export);
        return g;
    }

    internal static async Task<Ok<ManifestDto>> Manifest(HttpContext http, CmdManagerDbContext db, LibraryService lib, CancellationToken ct)
    {
        var userId = http.User.UserId();
        var commands = await db.Commands.AsNoTracking().Where(c => c.UserId == userId)
            .Select(c => new ManifestEntry(LibraryItemType.Command, c.Id, c.RelativePath, c.Sha256, c.Size, c.UpdatedUtc))
            .ToListAsync(ct);
        var assets = await db.Assets.AsNoTracking().Where(a => a.UserId == userId)
            .Select(a => new ManifestEntry(LibraryItemType.Asset, a.Id, a.RelativePath, a.Sha256, a.Size, a.UpdatedUtc))
            .ToListAsync(ct);
        var items = commands.Concat(assets).OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        return TypedResults.Ok(new ManifestDto(lib.Now, items));
    }

    internal static async Task<Results<Ok<ImportResult>, ValidationProblem>> Import(
        ImportRequest req, HttpContext http, LibraryService lib, CancellationToken ct)
    {
        if (req.Items is null || req.Items.Count == 0)
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["items"] = ["No items to import."] });
        if (req.Items.Count > 5000)
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["items"] = ["At most 5000 items per request."] });
        return TypedResults.Ok(await lib.ImportAsync(http.User.UserId(), req, ct));
    }

    /// <summary>Whole library as a zip (backup / manual copy). Built in a temp file, then streamed.</summary>
    internal static async Task<IResult> Export(HttpContext http, CmdManagerDbContext db, CancellationToken ct)
    {
        var userId = http.User.UserId();
        var tmp = new FileStream(Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
        using (var zip = new ZipArchive(tmp, ZipArchiveMode.Create, leaveOpen: true))
        {
            var commandIds = await db.Commands.Where(c => c.UserId == userId).Select(c => c.Id).ToListAsync(ct);
            foreach (var id in commandIds)
            {
                var c = await db.Commands.AsNoTracking().SingleAsync(x => x.Id == id, ct);
                await WriteEntryAsync(zip, c.RelativePath, c.ContentBytes(), c.UpdatedUtc, ct);
            }
            var assetIds = await db.Assets.Where(a => a.UserId == userId).Select(a => a.Id).ToListAsync(ct);
            foreach (var id in assetIds)
            {
                var a = await db.Assets.AsNoTracking().SingleAsync(x => x.Id == id, ct);
                await WriteEntryAsync(zip, a.RelativePath, a.Content, a.UpdatedUtc, ct);
            }
        }
        tmp.Position = 0;
        return Results.File(tmp, "application/zip", $"cmdmanager-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip");
    }

    private static async Task WriteEntryAsync(ZipArchive zip, string path, byte[] bytes, DateTime updatedUtc, CancellationToken ct)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        entry.LastWriteTime = new DateTimeOffset(DateTime.SpecifyKind(updatedUtc, DateTimeKind.Utc));
        await using var s = entry.Open();
        await s.WriteAsync(bytes, ct);
    }
}
