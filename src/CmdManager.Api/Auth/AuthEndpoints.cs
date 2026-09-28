using System.Text.RegularExpressions;
using CmdManager.Api.Data;
using CmdManager.Core.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CmdManager.Api.Auth;

public static partial class AuthEndpoints
{
    public const string RateLimitPolicy = "auth";

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._@\\-]{2,63}$")]
    private static partial Regex UserNamePattern();

    public static RouteGroupBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/auth").WithTags("Auth").RequireRateLimiting(RateLimitPolicy);

        g.MapGet("/config", (IOptions<AuthOptions> o) =>
            TypedResults.Ok(new AuthConfigDto(o.Value.AllowRegistration, o.Value.MinPasswordLength))).AllowAnonymous();

        g.MapPost("/register", Register).AllowAnonymous();
        g.MapPost("/login", Login).AllowAnonymous();
        // Anonymous because the access token may already be expired; the refresh token itself is the credential.
        g.MapPost("/refresh", Refresh).AllowAnonymous();
        g.MapPost("/logout", Logout).RequireAuthorization();
        g.MapGet("/me", Me).RequireAuthorization();
        return g;
    }

    internal static async Task<Results<Created<AuthResponse>, ValidationProblem, ProblemHttpResult>> Register(
        RegisterRequest req, HttpContext http, CmdManagerDbContext db, IOptions<AuthOptions> authOptions, TokenService tokens,
        IPasswordHasher<User> hasher, TimeProvider time, CancellationToken ct)
    {
        var opts = authOptions.Value;
        if (!opts.AllowRegistration)
            return TypedResults.Problem("Registration is disabled on this server.", statusCode: StatusCodes.Status403Forbidden);

        var errors = new Dictionary<string, string[]>();
        var userName = req.UserName?.Trim() ?? "";
        if (!UserNamePattern().IsMatch(userName))
            errors["userName"] = ["User name must be 3-64 characters: letters, digits, '.', '_', '-', '@', starting with a letter or digit."];
        if (string.IsNullOrEmpty(req.Password) || req.Password.Length < opts.MinPasswordLength)
            errors["password"] = [$"Password must be at least {opts.MinPasswordLength} characters."];
        else if (req.Password.Length > 256)
            errors["password"] = ["Password is too long."];
        var email = string.IsNullOrWhiteSpace(req.Email) ? null : req.Email.Trim();
        if (email is not null && (email.Length > 256 || !email.Contains('@')))
            errors["email"] = ["Email address is not valid."];
        if (errors.Count > 0)
            return TypedResults.ValidationProblem(errors);

        var normalized = userName.ToUpperInvariant();
        if (await db.Users.AnyAsync(u => u.NormalizedUserName == normalized, ct))
            return TypedResults.Problem("That user name is already taken.", statusCode: StatusCodes.Status409Conflict);

        var user = new User
        {
            UserName = userName,
            NormalizedUserName = normalized,
            Email = email,
            CreatedUtc = time.GetUtcNow().UtcDateTime
        };
        user.PasswordHash = hasher.HashPassword(user, req.Password!);
        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return TypedResults.Problem("That user name is already taken.", statusCode: StatusCodes.Status409Conflict);
        }

        user.LastLoginUtc = user.CreatedUtc;
        var auth = tokens.Issue(user, db);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"{http.Request.PathBase}/auth/me", auth);
    }

    internal static async Task<Results<Ok<AuthResponse>, ProblemHttpResult>> Login(
        LoginRequest req, CmdManagerDbContext db, IOptions<AuthOptions> authOptions, TokenService tokens,
        IPasswordHasher<User> hasher, TimeProvider time, CancellationToken ct)
    {
        var opts = authOptions.Value;
        var now = time.GetUtcNow().UtcDateTime;
        var normalized = (req.UserName ?? "").Trim().ToUpperInvariant();
        var user = await db.Users.SingleOrDefaultAsync(u => u.NormalizedUserName == normalized, ct);
        var invalid = TypedResults.Problem("Invalid user name or password.", statusCode: StatusCodes.Status401Unauthorized);
        if (user is null || string.IsNullOrEmpty(req.Password))
            return invalid;
        if (user.IsDisabled)
            return TypedResults.Problem("This account is disabled.", statusCode: StatusCodes.Status403Forbidden);
        if (user.LockoutEndUtc > now)
            return TypedResults.Problem("Too many failed attempts. Try again in a few minutes.", statusCode: StatusCodes.Status429TooManyRequests);

        var result = hasher.VerifyHashedPassword(user, user.PasswordHash, req.Password);
        if (result == PasswordVerificationResult.Failed)
        {
            user.AccessFailedCount++;
            if (opts.MaxFailedLogins > 0 && user.AccessFailedCount >= opts.MaxFailedLogins)
            {
                user.LockoutEndUtc = now.AddMinutes(opts.LockoutMinutes);
                user.AccessFailedCount = 0;
            }
            await db.SaveChangesAsync(ct);
            return invalid;
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
            user.PasswordHash = hasher.HashPassword(user, req.Password);
        user.AccessFailedCount = 0;
        user.LockoutEndUtc = null;
        user.LastLoginUtc = now;

        // Housekeeping: drop this user's expired/revoked refresh tokens.
        var stale = await db.RefreshTokens.Where(t => t.UserId == user.Id && (t.ExpiresUtc < now || t.RevokedUtc != null)).ToListAsync(ct);
        db.RefreshTokens.RemoveRange(stale);

        var auth = tokens.Issue(user, db);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(auth);
    }

    internal static async Task<Results<Ok<AuthResponse>, ProblemHttpResult>> Refresh(
        RefreshRequest req, CmdManagerDbContext db, TokenService tokens, TimeProvider time, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (string.IsNullOrWhiteSpace(req.RefreshToken))
            return TypedResults.Problem("Invalid refresh token.", statusCode: StatusCodes.Status401Unauthorized);
        var hash = TokenService.HashRefreshToken(req.RefreshToken);
        var token = await db.RefreshTokens.Include(t => t.User).SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null || token.RevokedUtc is not null || token.ExpiresUtc <= now || token.User is null || token.User.IsDisabled)
            return TypedResults.Problem("Invalid or expired refresh token. Please log in again.", statusCode: StatusCodes.Status401Unauthorized);

        token.RevokedUtc = now; // rotate: each refresh token works once
        var auth = tokens.Issue(token.User, db);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(auth);
    }

    internal static async Task<NoContent> Logout(
        RefreshRequest? req, HttpContext http, CmdManagerDbContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = http.User.UserId();
        if (!string.IsNullOrWhiteSpace(req?.RefreshToken))
        {
            var hash = TokenService.HashRefreshToken(req.RefreshToken);
            var token = await db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == hash && t.UserId == userId, ct);
            if (token is not null && token.RevokedUtc is null)
            {
                token.RevokedUtc = time.GetUtcNow().UtcDateTime;
                await db.SaveChangesAsync(ct);
            }
        }
        return TypedResults.NoContent();
    }

    internal static async Task<Results<Ok<UserDto>, NotFound>> Me(HttpContext http, CmdManagerDbContext db, CancellationToken ct)
    {
        var userId = http.User.UserId();
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ct);
        return user is null ? TypedResults.NotFound() : TypedResults.Ok(TokenService.ToDto(user));
    }
}
