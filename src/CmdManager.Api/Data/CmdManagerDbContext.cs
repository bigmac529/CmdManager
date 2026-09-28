using CmdManager.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CmdManager.Api.Data;

public sealed class CmdManagerDbContext(DbContextOptions<CmdManagerDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Command> Commands => Set<Command>();
    public DbSet<Asset> Assets => Set<Asset>();

    /// <summary>All timestamps are stored as UTC (datetime2); mark them Utc on read so JSON carries a trailing "Z".</summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder c)
    {
        c.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        c.Properties<DateTime?>().HaveConversion<UtcNullableDateTimeConverter>();
    }

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : v,
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private sealed class UtcNullableDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
        v => v.HasValue && v.Value.Kind == DateTimeKind.Local ? v.Value.ToUniversalTime() : v,
        v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.ToTable("Users");
            e.Property(x => x.UserName).HasMaxLength(64).IsRequired();
            e.Property(x => x.NormalizedUserName).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.NormalizedUserName).IsUnique();
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.PasswordHash).HasMaxLength(512).IsRequired();
        });

        b.Entity<RefreshToken>(e =>
        {
            e.ToTable("RefreshTokens");
            e.Property(x => x.TokenHash).HasMaxLength(64).IsUnicode(false).IsRequired();
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasOne(x => x.User).WithMany(u => u.RefreshTokens).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Command>(e =>
        {
            e.ToTable("Commands");
            ConfigureFile(e);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16).IsUnicode(false);
            e.Property(x => x.Tags).HasMaxLength(1000);
            e.HasIndex(x => new { x.UserId, x.Kind });
        });

        b.Entity<Asset>(e =>
        {
            e.ToTable("Assets");
            ConfigureFile(e);
            e.Property(x => x.Content).IsRequired();
        });
    }

    private static void ConfigureFile<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> e) where T : LibraryFile
    {
        e.Property(x => x.Name).HasMaxLength(LibraryPath.MaxSegmentLength).IsRequired();
        e.Property(x => x.Folder).HasMaxLength(LibraryPath.MaxLength).IsRequired();
        e.Property(x => x.RelativePath).HasMaxLength(LibraryPath.MaxLength).IsRequired();
        e.Property(x => x.PathKey).HasMaxLength(LibraryPath.MaxLength).IsRequired();
        e.Property(x => x.Description).HasMaxLength(1000);
        // Optimistic concurrency: UPDATE/DELETE include "WHERE Sha256 = <value read>", so a concurrent change → 409.
        e.Property(x => x.Sha256).HasMaxLength(64).IsUnicode(false).IsFixedLength().IsRequired().IsConcurrencyToken();
        e.HasIndex(x => new { x.UserId, x.PathKey }).IsUnique();
        e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
