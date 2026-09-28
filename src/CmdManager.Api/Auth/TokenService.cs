using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using CmdManager.Api.Data;
using CmdManager.Core;
using CmdManager.Core.Contracts;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CmdManager.Api.Auth;

public sealed class TokenService(IOptions<JwtOptions> jwtOptions, TimeProvider time)
{
    private readonly JwtOptions _jwt = jwtOptions.Value;

    public static SymmetricSecurityKey SigningKey(JwtOptions o) => new(Encoding.UTF8.GetBytes(o.Key));

    /// <summary>Issues an access token and a new refresh token (added to <paramref name="db"/>, caller saves).</summary>
    public AuthResponse Issue(User user, CmdManagerDbContext db)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var expires = now.AddMinutes(_jwt.AccessTokenMinutes);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _jwt.Issuer,
            Audience = _jwt.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.UniqueName, user.UserName),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
            ]),
            SigningCredentials = new SigningCredentials(SigningKey(_jwt), SecurityAlgorithms.HmacSha256)
        };
        var access = new JsonWebTokenHandler().CreateToken(descriptor);

        var refresh = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var refreshExpires = now.AddDays(_jwt.RefreshTokenDays);
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = HashRefreshToken(refresh),
            CreatedUtc = now,
            ExpiresUtc = refreshExpires
        });

        return new AuthResponse(access, expires, refresh, refreshExpires, ToDto(user));
    }

    public static string HashRefreshToken(string token) => ContentHash.Sha256Hex(Encoding.UTF8.GetBytes(token));

    public static UserDto ToDto(User u) => new(u.Id, u.UserName, u.Email, u.CreatedUtc);
}

public static class ClaimsPrincipalExtensions
{
    /// <summary>The authenticated user's id (from the JWT "sub" claim).</summary>
    public static int UserId(this ClaimsPrincipal principal) =>
        int.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out var id)
            ? id
            : throw new InvalidOperationException("Authenticated principal has no numeric 'sub' claim.");
}
