using System.Globalization;
using System.Linq.Expressions;
using System.Text;
using FieldTrack.Domain.Assets;
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

    /// <summary>Tracked field equipment, Archived ones included.</summary>
    public DbSet<Asset> Assets => Set<Asset>();

    /// <summary>Named sets of Assets.</summary>
    public DbSet<AssetGroup> AssetGroups => Set<AssetGroup>();

    /// <summary>Registered Devices, Archived ones included.</summary>
    public DbSet<Device> Devices => Set<Device>();

    /// <summary>Every period a Device was fitted to an Asset, open and ended.</summary>
    public DbSet<Attachment> Attachments => Set<Attachment>();

    /// <summary>
    /// Ends the open Attachments matching <paramref name="which"/> at <paramref name="at"/>, returning how many
    /// ended. Detach and both Archives end Attachments only through here, so whatever else must follow (resolving
    /// Alerts, #34) has one place to hook in. The caller holds the transaction.
    /// </summary>
    public Task<int> EndOpenAttachmentsAsync(Expression<Func<Attachment, bool>> which, DateTimeOffset at, CancellationToken cancellationToken) =>
        Attachments
            .Where(which)
            .Where(attachment => attachment.DetachedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(attachment => attachment.DetachedAt, at), cancellationToken);

    /// <summary>
    /// Creates the missing daily Reading partitions for the UTC days <paramref name="first"/> to <paramref name="last"/>
    /// (ADR 0003: own job, no pg_partman). Safe to run from several hosts at once.
    /// </summary>
    public Task CreateReadingPartitionsAsync(DateOnly first, DateOnly last, CancellationToken cancellationToken)
    {
        // One implicit transaction; the advisory lock makes concurrent runs take turns instead of racing on the names.
        var sql = new StringBuilder("SELECT pg_advisory_xact_lock(hashtext('readings partitions'));");
        for (var day = first; day <= last; day = day.AddDays(1))
        {
            sql.Append(CultureInfo.InvariantCulture,
                $"CREATE TABLE IF NOT EXISTS readings_{day:yyyyMMdd} PARTITION OF readings FOR VALUES FROM ('{day:yyyy-MM-dd}T00:00:00Z') TO ('{day.AddDays(1):yyyy-MM-dd}T00:00:00Z');");
        }

        return Database.ExecuteSqlRawAsync(sql.ToString(), cancellationToken);
    }

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

        builder.Entity<Asset>(asset =>
        {
            // Stored by name so hand-written SQL and exports read it without a lookup. The check keeps
            // hand-written SQL to the same names; renaming an AssetType member needs a data migration.
            asset.Property(a => a.Type).HasConversion<string>();
            asset.ToTable(table => table.HasCheckConstraint(
                "ck_assets_type", $"type IN ({string.Join(", ", Enum.GetNames<AssetType>().Select(name => $"'{name}'"))})"));
            // An Asset Group that still has Assets cannot be deleted (#24).
            asset.HasOne<AssetGroup>().WithMany().HasForeignKey(a => a.AssetGroupId).OnDelete(DeleteBehavior.Restrict);
        });

        // Telemetry names a Device only by serial number, so one serial is one Device, Archived or not.
        builder.Entity<Device>(device =>
        {
            device.HasIndex(d => d.SerialNumber).IsUnique();
            // A serial is one MQTT topic level: 1-64 of [A-Za-z0-9._-], so no '/', '+' or '#'.
            device.ToTable(table => table.HasCheckConstraint("ck_devices_serial_number", "serial_number ~ '^[A-Za-z0-9._-]{1,64}$'"));
        });

        builder.Entity<Attachment>(attachment =>
        {
            // Ingest stamps a Reading by the Attachment covering its Device time, so a period must not end
            // before it starts. A zero-length one is harmless: it covers no time.
            attachment.ToTable(table => table.HasCheckConstraint("ck_attachments_period", "detached_at IS NULL OR detached_at >= attached_at"));
            // Attachments are history: deleting an Asset or Device must decide what happens to them
            // (#25), so the database refuses to cascade.
            attachment.HasOne<Device>().WithMany().HasForeignKey(a => a.DeviceId).OnDelete(DeleteBehavior.Restrict);
            attachment.HasOne<Asset>().WithMany().HasForeignKey(a => a.AssetId).OnDelete(DeleteBehavior.Restrict);
            // A Device is on at most one Asset, and an Asset has at most one Device, at any time (spec #21).
            attachment.HasIndex(a => a.DeviceId).IsUnique().HasFilter("detached_at IS NULL").HasDatabaseName("ix_attachments_device_id_open");
            attachment.HasIndex(a => a.AssetId).IsUnique().HasFilter("detached_at IS NULL").HasDatabaseName("ix_attachments_asset_id_open");
            // History per Device or Asset in time order; also serves the Restrict checks on delete and
            // ingest's lookup of the Attachment covering a Reading's Device time.
            attachment.HasIndex(a => new { a.DeviceId, a.AttachedAt });
            attachment.HasIndex(a => new { a.AssetId, a.AttachedAt });
        });
    }
}
