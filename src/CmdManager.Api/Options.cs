using System.ComponentModel.DataAnnotations;

namespace CmdManager.Api;

/// <summary>Config section "Jwt". Jwt__Key must be supplied by the environment (never committed).</summary>
public sealed class JwtOptions
{
    public const string Section = "Jwt";

    /// <summary>HMAC-SHA256 signing key, at least 32 bytes (UTF-8). Env: Jwt__Key.</summary>
    [Required, MinLength(32, ErrorMessage = "Jwt:Key must be at least 32 characters.")]
    public string Key { get; set; } = "";

    public string Issuer { get; set; } = "CmdManager";
    public string Audience { get; set; } = "CmdManager.Client";

    [Range(1, 1440)]
    public int AccessTokenMinutes { get; set; } = 60;

    [Range(1, 365)]
    public int RefreshTokenDays { get; set; } = 30;
}

/// <summary>Config section "Auth".</summary>
public sealed class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>Open self-service registration. Set Auth__AllowRegistration=false once your account exists.</summary>
    public bool AllowRegistration { get; set; } = true;

    [Range(6, 128)]
    public int MinPasswordLength { get; set; } = 8;

    public int MaxFailedLogins { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 5;

    /// <summary>Requests per minute per client IP for {PathBase}/auth/*; 0 disables the limiter.</summary>
    public int RateLimitPerMinute { get; set; } = 30;
}

/// <summary>Config section "Library".</summary>
public sealed class LibraryOptions
{
    public const string Section = "Library";

    /// <summary>
    /// Largest single file (command or asset). Default 100 MB. SQL Server Express caps each database at 50 GB,
    /// so keep this modest.
    /// </summary>
    [Range(1024, 2L * 1024 * 1024 * 1024)]
    public long MaxFileBytes { get; set; } = 100L * 1024 * 1024;

    /// <summary>Largest request body (bulk import is JSON with base64, ~1.37x the raw bytes). 0 = derive from MaxFileBytes.</summary>
    public long MaxRequestBytes { get; set; }

    public long EffectiveMaxRequestBytes => MaxRequestBytes > 0 ? MaxRequestBytes : (long)(MaxFileBytes * 1.4) + 1024 * 1024;
}

/// <summary>Config section "Database".</summary>
public sealed class DatabaseOptions
{
    public const string Section = "Database";

    /// <summary>"SqlServer" (production) or "Sqlite" (local dev on non-Windows; schema via EnsureCreated).</summary>
    public string Provider { get; set; } = "SqlServer";

    /// <summary>Apply pending EF migrations at startup (SQL Server). The app pool login needs db_ddladmin for this.</summary>
    public bool MigrateOnStartup { get; set; } = true;

    public bool IsSqlite => Provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase);
}
