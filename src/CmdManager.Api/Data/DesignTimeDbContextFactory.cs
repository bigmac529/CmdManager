using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CmdManager.Api.Data;

/// <summary>
/// Used only by `dotnet ef` (migrations add / migrations script). Migrations target SQL Server; no connection is opened
/// for these commands, so the placeholder connection string below is never used to reach a server.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<CmdManagerDbContext>
{
    public CmdManagerDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("ConnectionStrings__CmdManager")
                 ?? "Server=.;Database=CmdManager;Trusted_Connection=True;TrustServerCertificate=True";
        var options = new DbContextOptionsBuilder<CmdManagerDbContext>().UseSqlServer(cs).Options;
        return new CmdManagerDbContext(options);
    }
}
