namespace CmdManager.Core.Contracts;

/// <summary>Body of POST /api/auth/register.</summary>
public sealed record RegisterRequest(string UserName, string Password, string? Email = null);

/// <summary>Body of POST /api/auth/login.</summary>
public sealed record LoginRequest(string UserName, string Password);

/// <summary>Body of POST /api/auth/refresh and POST /api/auth/logout.</summary>
public sealed record RefreshRequest(string RefreshToken);

public sealed record UserDto(int Id, string UserName, string? Email, DateTime CreatedUtc);

/// <summary>Returned by register, login and refresh.</summary>
public sealed record AuthResponse(
    string AccessToken,
    DateTime ExpiresUtc,
    string RefreshToken,
    DateTime RefreshExpiresUtc,
    UserDto User);

/// <summary>GET /api/auth/config (anonymous): lets the client hide "Create account" when registration is closed.</summary>
public sealed record AuthConfigDto(bool AllowRegistration, int MinPasswordLength);
