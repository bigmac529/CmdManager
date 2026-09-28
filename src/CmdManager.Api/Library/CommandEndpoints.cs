using CmdManager.Api.Auth;
using CmdManager.Api.Data;
using CmdManager.Core;
using CmdManager.Core.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CmdManager.Api.Library;

public static class CommandEndpoints
{
    public static RouteGroupBuilder MapCommandEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/commands").WithTags("Commands").RequireAuthorization();
        g.MapGet("/", List);
        g.MapGet("/{id:int}", Get);
        g.MapGet("/{id:int}/content", Content);
        g.MapPost("/", Create);
        g.MapPut("/{id:int}", Update);
        // raw body; optional If-Match: <sha256 the client last saw> → 409 when the stored content differs
        g.MapPut("/{id:int}/content", ReplaceContent);
        g.MapDelete("/{id:int}", Delete);
        return g;
    }

    internal static async Task<Ok<List<CommandSummaryDto>>> List(
        HttpContext http, CmdManagerDbContext db, string? search, CommandKind? kind, string? folder, CancellationToken ct)
    {
        var userId = http.User.UserId();
        var q = db.Commands.AsNoTracking().Where(c => c.UserId == userId);
        if (kind is not null)
            q = q.Where(c => c.Kind == kind);
        if (folder is not null)
        {
            var f = folder.Trim().Replace('\\', '/').Trim('/');
            q = q.Where(c => c.Folder == f);
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = "%" + EscapeLike(search.Trim()) + "%";
            q = q.Where(c => EF.Functions.Like(c.RelativePath, pattern, "\\") ||
                             EF.Functions.Like(c.Description!, pattern, "\\") ||
                             EF.Functions.Like(c.Tags!, pattern, "\\"));
        }

        var rows = await q.OrderBy(c => c.PathKey)
            .Select(c => new { c.Id, c.Name, c.Folder, c.RelativePath, c.Kind, c.Description, c.Tags, c.IsBinary, c.Size, c.Sha256, c.CreatedUtc, c.UpdatedUtc })
            .ToListAsync(ct);
        return TypedResults.Ok(rows.Select(c => new CommandSummaryDto(c.Id, c.Name, c.Folder, c.RelativePath, c.Kind, c.Description,
            LibraryService.SplitTags(c.Tags), c.IsBinary, c.Size, c.Sha256, c.CreatedUtc, c.UpdatedUtc)).ToList());
    }

    internal static async Task<Results<Ok<CommandDto>, NotFound>> Get(int id, HttpContext http, CmdManagerDbContext db, CancellationToken ct)
    {
        var userId = http.User.UserId();
        var c = await db.Commands.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
        return c is null ? TypedResults.NotFound() : TypedResults.Ok(LibraryService.ToDto(c));
    }

    internal static async Task<Results<FileContentHttpResult, NotFound>> Content(int id, HttpContext http, CmdManagerDbContext db, CancellationToken ct)
    {
        var userId = http.User.UserId();
        var c = await db.Commands.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
        if (c is null)
            return TypedResults.NotFound();
        http.Response.Headers["X-Content-Sha256"] = c.Sha256;
        return TypedResults.File(c.ContentBytes(), "application/octet-stream", c.Name);
    }

    internal static async Task<Results<Created<CommandDto>, ValidationProblem, ProblemHttpResult>> Create(
        CommandUpsertRequest req, HttpContext http, CmdManagerDbContext db, LibraryService lib, CancellationToken ct)
    {
        var userId = http.User.UserId();
        if (Validate(req, lib, isCreate: true) is { } problem)
            return problem;
        var path = LibraryPath.Parse(req.RelativePath);
        if (await lib.PathTakenAsync(userId, path.Key, ct: ct))
            return TypedResults.Problem($"'{path.Value}' already exists in your library.", statusCode: StatusCodes.Status409Conflict);

        var now = lib.Now;
        var cmd = new Command { UserId = userId, CreatedUtc = now, UpdatedUtc = now };
        cmd.SetPath(path);
        cmd.Kind = req.Kind ?? path.Kind;
        cmd.Description = LibraryService.CleanDescription(req.Description);
        cmd.Tags = LibraryService.NormalizeTags(req.Tags);
        if (req.BinaryContent is not null)
            cmd.SetBinary(req.BinaryContent);
        else
            cmd.SetText(req.TextContent ?? "");
        db.Commands.Add(cmd);
        if (await TrySaveAsync(db, path.Value, ct) is { } conflict)
            return conflict;
        return TypedResults.Created($"/api/commands/{cmd.Id}", LibraryService.ToDto(cmd));
    }

    internal static async Task<Results<Ok<CommandDto>, NotFound, ValidationProblem, ProblemHttpResult>> Update(
        int id, CommandUpsertRequest req, HttpContext http, CmdManagerDbContext db, LibraryService lib, CancellationToken ct)
    {
        var userId = http.User.UserId();
        var cmd = await db.Commands.SingleOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
        if (cmd is null)
            return TypedResults.NotFound();
        if (Validate(req, lib, isCreate: false) is { } problem)
            return problem;
        if (req.ExpectedSha256 is not null && !ContentHash.Equal(req.ExpectedSha256, cmd.Sha256))
            return TypedResults.Problem("This command was changed elsewhere since you opened it. Reload it and try again.", statusCode: StatusCodes.Status409Conflict);

        var path = LibraryPath.Parse(req.RelativePath);
        if (path.Key != cmd.PathKey)
        {
            if (await lib.PathTakenAsync(userId, path.Key, exceptCommandId: cmd.Id, ct: ct))
                return TypedResults.Problem($"'{path.Value}' already exists in your library.", statusCode: StatusCodes.Status409Conflict);
            cmd.SetPath(path);
        }
        else if (path.Value != cmd.RelativePath)
            cmd.SetPath(path); // case-only rename

        cmd.Kind = req.Kind ?? path.Kind;
        cmd.Description = LibraryService.CleanDescription(req.Description);
        cmd.Tags = LibraryService.NormalizeTags(req.Tags);
        if (req.BinaryContent is not null)
            cmd.SetBinary(req.BinaryContent);
        else if (req.TextContent is not null)
            cmd.SetText(req.TextContent);
        cmd.UpdatedUtc = lib.Now;
        if (await TrySaveAsync(db, path.Value, ct) is { } conflict)
            return conflict;
        return TypedResults.Ok(LibraryService.ToDto(cmd));
    }

    /// <summary>Content-only update used by "Sync local changes to DB" (keeps name, description and tags).</summary>
    internal static async Task<Results<Ok<CommandDto>, NotFound, ProblemHttpResult>> ReplaceContent(
        int id, HttpRequest request, CmdManagerDbContext db, LibraryService lib, CancellationToken ct)
    {
        var userId = request.HttpContext.User.UserId();
        var cmd = await db.Commands.SingleOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
        if (cmd is null)
            return TypedResults.NotFound();
        var expected = LibraryService.ExpectedHash(request.Headers.IfMatch.ToString());
        if (expected is not null && !ContentHash.Equal(expected, cmd.Sha256))
            return TypedResults.Problem("The command was changed on the server since your last sync.", statusCode: StatusCodes.Status409Conflict);
        if (request.ContentLength is { } len && lib.CheckSize(len) is { } e1)
            return TypedResults.Problem(e1, statusCode: StatusCodes.Status413PayloadTooLarge);

        using var ms = new MemoryStream();
        await request.Body.CopyToAsync(ms, ct);
        if (lib.CheckSize(ms.Length) is { } e2)
            return TypedResults.Problem(e2, statusCode: StatusCodes.Status413PayloadTooLarge);
        var bytes = ms.ToArray();
        if (cmd.Kind is not (CommandKind.Exe or CommandKind.Lnk) && TextContent.TryDecode(bytes, out var text))
            cmd.SetText(text);
        else
            cmd.SetBinary(bytes);
        cmd.UpdatedUtc = lib.Now;
        if (await TrySaveAsync(db, cmd.RelativePath, ct) is { } conflict)
            return conflict;
        return TypedResults.Ok(LibraryService.ToDto(cmd));
    }

    /// <summary>Optional ?expectedSha256= makes the delete fail with 409 if the command changed since the client saw it.</summary>
    internal static async Task<Results<NoContent, NotFound, ProblemHttpResult>> Delete(
        int id, string? expectedSha256, HttpContext http, CmdManagerDbContext db, CancellationToken ct)
    {
        var userId = http.User.UserId();
        var current = await db.Commands.Where(x => x.Id == id && x.UserId == userId).Select(x => x.Sha256).SingleOrDefaultAsync(ct);
        if (current is null)
            return TypedResults.NotFound();
        var expected = LibraryService.ExpectedHash(expectedSha256);
        if (expected is not null && !ContentHash.Equal(expected, current))
            return TypedResults.Problem("The command was changed on the server since your last sync.", statusCode: StatusCodes.Status409Conflict);
        var n = await db.Commands.Where(x => x.Id == id && x.UserId == userId && x.Sha256 == current).ExecuteDeleteAsync(ct);
        return n == 0
            ? TypedResults.Problem("The command was changed on the server meanwhile.", statusCode: StatusCodes.Status409Conflict)
            : TypedResults.NoContent();
    }

    private static ValidationProblem? Validate(CommandUpsertRequest req, LibraryService lib, bool isCreate)
    {
        var errors = new Dictionary<string, string[]>();
        if (!LibraryPath.TryParse(req.RelativePath, out _, out var pathError))
            errors["relativePath"] = [pathError];
        if (req.TextContent is not null && req.BinaryContent is not null)
            errors["content"] = ["Provide either textContent or binaryContent, not both."];
        var size = req.BinaryContent?.LongLength ?? (req.TextContent is null ? 0 : TextContent.ToBytes(req.TextContent).LongLength);
        if (lib.CheckSize(size) is { } sizeError)
            errors["content"] = [sizeError];
        if (req.Description is { Length: > 1000 })
            errors["description"] = ["Description is too long (max 1000 characters)."];
        try
        {
            LibraryService.NormalizeTags(req.Tags);
        }
        catch (ArgumentException ex)
        {
            errors["tags"] = [ex.Message];
        }
        return errors.Count > 0 ? TypedResults.ValidationProblem(errors) : null;
    }

    /// <summary>Saves; returns a 409 problem when the hash concurrency token or the unique path index was violated.</summary>
    internal static async Task<ProblemHttpResult?> TrySaveAsync(CmdManagerDbContext db, string path, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            return TypedResults.Problem($"'{path}' was changed on the server meanwhile. Reload and try again.", statusCode: StatusCodes.Status409Conflict);
        }
        catch (DbUpdateException)
        {
            return TypedResults.Problem($"'{path}' already exists in your library.", statusCode: StatusCodes.Status409Conflict);
        }
    }

    internal static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");
}
