using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace CmdManager.Api.Data;

/// <summary>Healthy only when the database is reachable and (SQL Server) no EF migrations are pending.</summary>
public sealed class DatabaseHealthCheck(CmdManagerDbContext db, IOptions<DatabaseOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var data = new Dictionary<string, object> { ["provider"] = options.Value.Provider };
        try
        {
            var connected = await db.Database.CanConnectAsync(ct);
            data["connected"] = connected;
            if (!connected)
                return HealthCheckResult.Unhealthy("Cannot connect to the database.", data: data);

            if (!options.Value.IsSqlite && db.Database.IsRelational() && db.Database.GetMigrations().Any())
            {
                var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
                var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();
                data["pendingMigrations"] = pending.Count;
                data["lastMigration"] = applied.LastOrDefault() ?? "";
                if (pending.Count > 0)
                    return HealthCheckResult.Unhealthy($"{pending.Count} pending migration(s): {string.Join(", ", pending)}", data: data);
            }
            else
            {
                data["pendingMigrations"] = 0;
            }

            return HealthCheckResult.Healthy("Database connected; schema up to date.", data);
        }
        catch (Exception ex)
        {
            data["connected"] = false;
            return HealthCheckResult.Unhealthy("Database check failed: " + ex.GetType().Name, data: data);
        }
    }
}
