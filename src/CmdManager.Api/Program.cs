using System.Threading.RateLimiting;
using CmdManager.Api;
using CmdManager.Api.Auth;
using CmdManager.Api.Data;
using CmdManager.Api.Library;
using CmdManager.Core.Http;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// ---- options (all read lazily so tests / env vars can override) ----
builder.Services.AddOptions<JwtOptions>().Bind(builder.Configuration.GetSection(JwtOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<AuthOptions>().Bind(builder.Configuration.GetSection(AuthOptions.Section)).ValidateDataAnnotations();
builder.Services.AddOptions<LibraryOptions>().Bind(builder.Configuration.GetSection(LibraryOptions.Section)).ValidateDataAnnotations();
builder.Services.AddOptions<DatabaseOptions>().Bind(builder.Configuration.GetSection(DatabaseOptions.Section));

// ---- database: ConnectionStrings:CmdManager (env ConnectionStrings__CmdManager) ----
builder.Services.AddDbContext<CmdManagerDbContext>((sp, o) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var db = sp.GetRequiredService<IOptions<DatabaseOptions>>().Value;
    var cs = config.GetConnectionString("CmdManager");
    if (string.IsNullOrWhiteSpace(cs))
        throw new InvalidOperationException("Connection string 'CmdManager' is not configured (ConnectionStrings__CmdManager).");
    if (db.IsSqlite)
        o.UseSqlite(cs);
    else
        o.UseSqlServer(cs, sql => sql.EnableRetryOnFailure(3));
});

// ---- auth: PBKDF2 password hashes (Identity's PasswordHasher) + JWT bearer ----
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<LibraryService>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtOptions>>((o, jwtOptions) =>
    {
        var jwt = jwtOptions.Value;
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = TokenService.SigningKey(jwt),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "unique_name"
        };
    });
// Fallback policy: every endpoint requires an authenticated user unless it is explicitly marked AllowAnonymous
// (only "/", /health and /api/auth/{config,register,login,refresh}).
builder.Services.AddAuthorization(o => o.FallbackPolicy = new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
    .RequireAuthenticatedUser()
    .Build());

// ---- rate limit /api/auth/* per client IP ----
builder.Services.AddRateLimiter(_ => { });
builder.Services.AddOptions<RateLimiterOptions>().Configure<IOptions<AuthOptions>>((o, auth) =>
{
    var permits = auth.Value.RateLimitPerMinute;
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(AuthEndpoints.RateLimitPolicy, http => permits <= 0
        ? RateLimitPartition.GetNoLimiter("none")
        : RateLimitPartition.GetFixedWindowLimiter(http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

// ---- request size limits (per-file limit is enforced by the endpoints; this caps whole bodies) ----
builder.Services.AddOptions<KestrelServerOptions>().Configure<IOptions<LibraryOptions>>((o, lib) => o.Limits.MaxRequestBodySize = lib.Value.EffectiveMaxRequestBytes);
builder.Services.AddOptions<IISServerOptions>().Configure<IOptions<LibraryOptions>>((o, lib) => o.MaxRequestBodySize = lib.Value.EffectiveMaxRequestBytes);
builder.Services.AddOptions<FormOptions>().Configure<IOptions<LibraryOptions>>((o, lib) => o.MultipartBodyLengthLimit = lib.Value.MaxFileBytes + 1024 * 1024);

builder.Services.ConfigureHttpJsonOptions(o => CmdManagerJson.Configure(o.SerializerOptions));
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
if (!app.Environment.IsDevelopment())
    app.UseHsts();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous(); // /openapi/v1.json
    app.UseSwaggerUI(o =>
    {
        o.SwaggerEndpoint("/openapi/v1.json", "CmdManager API v1");
        o.RoutePrefix = "swagger";
    });
}

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapGet("/", () => Results.Text("CmdManager API. See /health.", "text/plain")).ExcludeFromDescription().AllowAnonymous();
app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResultStatusCodes =
    {
        [HealthStatus.Healthy] = StatusCodes.Status200OK,
        [HealthStatus.Degraded] = StatusCodes.Status200OK,
        [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
    },
    ResponseWriter = async (http, report) =>
    {
        http.Response.ContentType = "application/json";
        await http.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(e => e.Key, e => new
            {
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                data = e.Value.Data
            })
        });
    }
}).AllowAnonymous();

app.MapAuthEndpoints();
app.MapCommandEndpoints();
app.MapAssetEndpoints();
app.MapLibraryEndpoints();

await app.InitializeDatabaseAsync();
await app.RunAsync();

public partial class Program;
