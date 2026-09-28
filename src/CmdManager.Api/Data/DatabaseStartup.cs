using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CmdManager.Api.Data;

public static class DatabaseStartup
{
    /// <summary>
    /// SQL Server: applies pending EF migrations when Database:MigrateOnStartup is true (default), logging each one.
    /// SQLite (local dev only): creates the schema from the model with EnsureCreated.
    /// </summary>
    public static async Task InitializeDatabaseAsync(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
        if (!options.MigrateOnStartup)
            return;

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CmdManagerDbContext>();
        var log = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("CmdManager.Database");

        if (options.IsSqlite)
        {
            if (await db.Database.EnsureCreatedAsync())
                log.LogInformation("Created SQLite database schema (EnsureCreated).");
            return;
        }

        var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
        if (pending.Count == 0)
        {
            log.LogInformation("Database schema is up to date ({Count} migrations applied).", (await db.Database.GetAppliedMigrationsAsync()).Count());
            return;
        }

        log.LogInformation("Applying {Count} pending migration(s)...", pending.Count);
        await db.Database.MigrateAsync();
        foreach (var name in pending)
            log.LogInformation("Applied migration {Migration}", name);
    }
}
