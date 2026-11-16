using FieldTrack.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace FieldTrack.Infrastructure.Persistence;

/// <summary>
/// The EF Core unit of work over the FieldTrack PostgreSQL database. Migrations in this
/// assembly own the schema; the ingest write path (ADR 0004) uses hand-written SQL beside it.
/// Users and roles live here too, through ASP.NET Core Identity (ADR 0002).
/// </summary>
public sealed class FieldTrackDbContext(DbContextOptions<FieldTrackDbContext> options)
    : IdentityDbContext<FieldTrackUser, IdentityRole<Guid>, Guid>(options)
{
    /// <summary>Refresh tokens, stored hashed (ADR 0002).</summary>
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Positions and Geofence areas are PostGIS geography columns (ADR 0003, spec #21 Storage).
        builder.HasPostgresExtension("postgis");

        // Identity names its tables and indexes explicitly, which the snake_case convention leaves alone.
        builder.Entity<FieldTrackUser>(user =>
        {
            user.ToTable("users");
            user.HasIndex(u => u.NormalizedEmail).HasDatabaseName("ix_users_normalized_email");
            user.HasIndex(u => u.NormalizedUserName).HasDatabaseName("ix_users_normalized_user_name");
        });
        builder.Entity<IdentityRole<Guid>>(role =>
        {
            role.ToTable("roles");
            role.HasIndex(r => r.NormalizedName).HasDatabaseName("ix_roles_normalized_name");
        });
        builder.Entity<IdentityUserRole<Guid>>().ToTable("user_roles");
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens");
        builder.Entity<IdentityRoleClaim<Guid>>().ToTable("role_claims");

        builder.Entity<RefreshToken>(refreshToken =>
        {
            refreshToken.HasIndex(token => token.TokenHash).IsUnique();
            // Deleting a user deletes their refresh tokens.
            refreshToken.HasOne<FieldTrackUser>().WithMany().HasForeignKey(token => token.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
