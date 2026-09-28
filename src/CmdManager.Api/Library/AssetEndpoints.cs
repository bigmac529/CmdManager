using CmdManager.Api.Auth;
using CmdManager.Api.Data;
using CmdManager.Core;
using CmdManager.Core.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CmdManager.Api.Library;

public static class AssetEndpoints
{
    public static RouteGroupBuilder MapAssetEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/assets").WithTags("Assets").RequireAuthorization();
        g.MapGet("/", List);
        g.MapGet("/{id:int}", Get);
        g.MapGet("/{id:int}/content", Content);
        // multipart/form-data: path (text), file (binary), description (optional text)
        g.MapPost("/", Upload).DisableAntiforgery();
        // raw body (application/octet-stream) or multipart with a "file" part
        g.MapPut("/{id:int}/content", ReplaceContent).DisableAntiforgery();
        g.MapPut("/{id:int}", Update);
        g.MapDelete("/{id:int}", Delete);
        return g;
    }

    internal static async Task<Ok<List<AssetDto>>> List(HttpContext http, CmdManagerDbContext db, string? search, string? folder, CancellationToken ct)
    {
        var userId = http.User.UserId();
        var q = db.Assets.AsNoTracking().Where(a => a.UserId == userId);
        if (folder is not null)
        {
            var f = folder.Trim().Replace('\\', '/').Trim('/');
            q = q.Where(a => a.Folder == f);
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = "%" + CommandEndpoints.EscapeLike(search.Trim()) + "%";
            q = q.Where(a => EF.Functions.Like(a.RelativePath, pattern, "\\") || EF.Functions.Like(a.Description!, pattern, "\\"));
        }
        var rows = await q.OrderBy(a => a.PathKey)
            .Select(a => new AssetDto(a.Id, a.Name, a.Folder, a.RelativePath, a.Description, a.Size, a.Sha256, a.CreatedUtc, a.UpdatedUtc))
            .ToListAsync(ct);
        return TypedResults.Ok(rows);
    }

    internal static async Task<Results<Ok<AssetDto>, NotFound>> Get(int id, HttpContext http, CmdManagerDbContext db, CancellationToken ct)
    {
        var userId = http.User.UserId();
        var a = await db.Assets.AsNoTracking().Where(x => x.Id == id && x.UserId == userId)
            .Select(x => new AssetDto(x.Id, x.Name, x.Folder, x.RelativePath, x.Description, x.Size, x.Sha256, x.CreatedUtc, x.UpdatedUtc))
            .SingleOrDefaultAsync(ct);
        return a is null ? TypedResults.NotFound() : TypedResults.Ok(a);
    }

    internal static async Task<Results<FileContentHttpResult, NotFound>> Content(int id, HttpContext http, CmdManagerDbContext db, CancellationToken ct)
    {
        var userId = http.User.UserId();
        var a = await db.Assets.AsNoTracking().Where(x => x.Id == id && x.UserId == userId)
            .Select(x => new { x.Name, x.Sha256, x.Content }).SingleOrDefaultAsync(ct);
        if (a is null)
            return TypedResults.NotFound();
        http.Response.Headers["X-Content-Sha256"] = a.Sha256;
        return TypedResults.File(a.Content, "application/octet-stream", a.Name);
    }

    internal static async Task<Results<Created<AssetDto>, ValidationProblem, ProblemHttpResult>> Upload(
        [FromForm] string path, IFormFile file, [FromForm] string? description,
        HttpContext http, CmdManagerDbContext db, LibraryService lib, CancellationToken ct)
    {
        var userId = http.User.UserId();
        if (!LibraryPath.TryParse(path, out var lp, out var pathError))
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["path"] = [pathError] });
        if (lib.CheckSize(file.Length) is { } sizeError)
            return TypedResults.Problem(sizeError, statusCode: StatusCodes.Status413PayloadTooLarge);
        if (description is { Length: > 1000 })
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["description"] = ["Description is too long (max 1000 characters)."] });
        if (await lib.PathTakenAsync(userId, lp.Key, ct: ct))
            return TypedResults.Problem($"'{lp.Value}' already exists in your library.", statusCode: StatusCodes.Status409Conflict);

        var now = lib.Now;
        var asset = new Asset { UserId = userId, CreatedUtc = now, UpdatedUtc = now, Description = LibraryService.CleanDescription(description) };
        asset.SetPath(lp);
        asset.SetContent(await ReadAllAsync(file, ct));
        db.Assets.Add(asset);
        if (await CommandEndpoints.TrySaveAsync(db, lp.Value, ct) is { } conflict)
            return conflict;
        return TypedResults.Created($"/api/assets/{asset.Id}", LibraryService.ToDto(asset));
    }

    internal static async Task<Results<Ok<AssetDto>, NotFound, ProblemHttpResult>> ReplaceContent(
        int id, HttpRequest request, CmdManagerDbContext db, LibraryService lib, CancellationToken ct)
    {
        var userId = request.HttpContext.User.UserId();
        var asset = await db.Assets.SingleOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
        if (asset is null)
            return TypedResults.NotFound();
        var expected = LibraryService.ExpectedHash(request.Headers.IfMatch.ToString());
        if (expected is not null && !ContentHash.Equal(expected, asset.Sha256))
            return TypedResults.Problem("The asset was changed on the server since your last sync.", statusCode: StatusCodes.Status409Conflict);

        byte[] bytes;
        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file is null)
                return TypedResults.Problem("Multipart body has no file.", statusCode: StatusCodes.Status400BadRequest);
            if (lib.CheckSize(file.Length) is { } e1)
                return TypedResults.Problem(e1, statusCode: StatusCodes.Status413PayloadTooLarge);
            bytes = await ReadAllAsync(file, ct);
        }
        else
        {
            if (request.ContentLength is { } len && lib.CheckSize(len) is { } e2)
                return TypedResults.Problem(e2, statusCode: StatusCodes.Status413PayloadTooLarge);
            using var ms = new MemoryStream();
            await request.Body.CopyToAsync(ms, ct);
            if (lib.CheckSize(ms.Length) is { } e3)
                return TypedResults.Problem(e3, statusCode: StatusCodes.Status413PayloadTooLarge);
            bytes = ms.ToArray();
        }

        asset.SetContent(bytes);
        asset.UpdatedUtc = lib.Now;
        if (await CommandEndpoints.TrySaveAsync(db, asset.RelativePath, ct) is { } conflict)
            return conflict;
        return TypedResults.Ok(LibraryService.ToDto(asset));
    }

    internal static async Task<Results<Ok<AssetDto>, NotFound, ValidationProblem, ProblemHttpResult>> Update(
        int id, AssetUpdateRequest req, HttpContext http, CmdManagerDbContext db, LibraryService lib, CancellationToken ct)
    {
        var userId = http.User.UserId();
        var asset = await db.Assets.SingleOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
        if (asset is null)
            return TypedResults.NotFound();
        if (!LibraryPath.TryParse(req.RelativePath, out var lp, out var pathError))
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["relativePath"] = [pathError] });
        if (req.Description is { Length: > 1000 })
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["description"] = ["Description is too long (max 1000 characters)."] });
        if (lp.Key != asset.PathKey && await lib.PathTakenAsync(userId, lp.Key, exceptAssetId: asset.Id, ct: ct))
            return TypedResults.Problem($"'{lp.Value}' already exists in your library.", statusCode: StatusCodes.Status409Conflict);

        asset.SetPath(lp);
        asset.Description = LibraryService.CleanDescription(req.Description);
        asset.UpdatedUtc = lib.Now;
        if (await CommandEndpoints.TrySaveAsync(db, lp.Value, ct) is { } conflict)
            return conflict;
        return TypedResults.Ok(LibraryService.ToDto(asset));
    }

    internal static async Task<Results<NoContent, NotFound, ProblemHttpResult>> Delete(
        int id, string? expectedSha256, HttpContext http, CmdManagerDbContext db, CancellationToken ct)
    {
        var userId = http.User.UserId();
        var current = await db.Assets.Where(x => x.Id == id && x.UserId == userId).Select(x => x.Sha256).SingleOrDefaultAsync(ct);
        if (current is null)
            return TypedResults.NotFound();
        var expected = LibraryService.ExpectedHash(expectedSha256);
        if (expected is not null && !ContentHash.Equal(expected, current))
            return TypedResults.Problem("The asset was changed on the server since your last sync.", statusCode: StatusCodes.Status409Conflict);
        var n = await db.Assets.Where(x => x.Id == id && x.UserId == userId && x.Sha256 == current).ExecuteDeleteAsync(ct);
        return n == 0
            ? TypedResults.Problem("The asset was changed on the server meanwhile.", statusCode: StatusCodes.Status409Conflict)
            : TypedResults.NoContent();
    }

    private static async Task<byte[]> ReadAllAsync(IFormFile file, CancellationToken ct)
    {
        using var ms = new MemoryStream((int)Math.Min(file.Length, int.MaxValue));
        await file.CopyToAsync(ms, ct);
        return ms.ToArray();
    }
}
