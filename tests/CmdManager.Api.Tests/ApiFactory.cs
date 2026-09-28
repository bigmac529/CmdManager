using CmdManager.Core.Contracts;
using CmdManager.Core.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace CmdManager.Api.Tests;

/// <summary>
/// Runs the real API in-memory with the SQLite provider (shared in-memory database kept alive by an open connection).
/// Startup uses the same path as local SQLite dev: Database:Provider=Sqlite → EnsureCreated.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString = $"Data Source=file:cmdlib-{Guid.NewGuid():N}?mode=memory&cache=shared";
    private readonly SqliteConnection _keepAlive;

    /// <summary>Clients talk to http://localhost/api/ like production (https://cmdmanager.socha3.com/api/).</summary>
    public static readonly Uri ApiBase = new("http://localhost/api/");

    public ApiFactory()
    {
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
        ClientOptions.BaseAddress = ApiBase;
    }

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        client.BaseAddress = ApiBase;
    }

    public Dictionary<string, string?> Settings { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:CmdManager", _connectionString);
        builder.UseSetting("Database:Provider", "Sqlite");
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Jwt:Key", "unit-test-signing-key-not-a-secret-0123456789abcdef");
        builder.UseSetting("Auth:RateLimitPerMinute", "0");
        builder.UseSetting("Library:MaxFileBytes", (1024 * 1024).ToString());
        foreach (var (k, v) in Settings)
            builder.UseSetting(k, v);
    }

    /// <summary>Same wiring as the desktop app: one HttpClient with the AuthTokenHandler.</summary>
    public CmdManagerApiClient NewClient()
    {
        var session = new AuthSession();
        return new CmdManagerApiClient(CreateDefaultClient(new AuthTokenHandler(session, ApiBase)), session);
    }

    public async Task<CmdManagerApiClient> RegisteredClientAsync(string? userName = null, string password = "correct horse battery")
    {
        var client = NewClient();
        await client.RegisterAsync(new RegisterRequest(userName ?? "user" + Guid.NewGuid().ToString("N")[..10], password));
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _keepAlive.Dispose();
    }
}
