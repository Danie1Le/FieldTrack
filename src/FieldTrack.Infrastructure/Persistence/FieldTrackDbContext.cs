using System.Globalization;
using System.Linq.Expressions;
using System.Text;
using FieldTrack.Domain.Alerts;
using FieldTrack.Domain.Assets;
using FieldTrack.Domain.Telemetry;
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

    /// <summary>Alert Rules, enabled and disabled.</summary>
    public DbSet<AlertRule> AlertRules => Set<AlertRule>();

    /// <summary>Alerts, Active and Resolved.</summary>
    public DbSet<Alert> Alerts => Set<Alert>();

    /// <summary>Rule state per Alert Rule and Asset, which ingest keeps (ADR 0005).</summary>
    public DbSet<AlertRuleState> AlertRuleStates => Set<AlertRuleState>();

    /// <summary>Telemetry messages ingest refused, with their reasons.</summary>
    public DbSet<RejectedMessage> RejectedMessages => Set<RejectedMessage>();

    /// <summary>
    /// Bumps the Alert Rule version (ADR 0005) in the caller's transaction, so the ingest worker reloads its rules and
    /// rule state before its next batch. Call it once in any transaction that changes what ingest evaluates or caches
    /// (Alert Rules, their Alerts, rule state). Ingest reads the version <c>FOR SHARE</c> after its Device and Asset
    /// locks, so this row lock waits for a running batch and holds the next one until the change commits: the change
    /// and a batch never interleave. Lock order (ADR 0005): call it after every Device or Asset lock the transaction
    /// takes, including the Asset a rule's foreign key names, and before touching Alert Rules, Alerts or rule state.
    /// </summary>
    public Task BumpAlertRuleVersionAsync(CancellationToken cancellationToken) =>
        Database.ExecuteSqlRawAsync("UPDATE alert_rule_version SET version = version + 1", cancellationToken);

    /// <summary>
    /// Resolves the Active Alerts of the Alert Rule <paramref name="ruleId"/> as the platform, with
    /// <paramref name="reason"/> at <paramref name="at"/>, and returns the ids of exactly the Alerts it resolved, for
    /// the live push once the caller commits. A user who resolved one first keeps their resolution (#10). Call
    /// <see cref="BumpAlertRuleVersionAsync"/> first.
    /// </summary>
    public Task<List<Guid>> ResolveActiveAlertsOfRuleAsync(Guid ruleId, ResolveReason reason, DateTimeOffset at, CancellationToken cancellationToken) =>
        ResolveActiveAlertsAsync([ruleId], [], reason, at, cancellationToken);

    /// <summary>
    /// Resolves the Active Alerts of the Assets <paramref name="assetIds"/> as the platform, with
    /// <paramref name="reason"/> at <paramref name="at"/>, and returns the ids of exactly the Alerts it resolved, for
    /// the live push once the caller commits. A user who resolved one first keeps their resolution (#10). Call
    /// <see cref="BumpAlertRuleVersionAsync"/> first.
    /// </summary>
    public Task<List<Guid>> ResolveActiveAlertsOfAssetsAsync(Guid[] assetIds, ResolveReason reason, DateTimeOffset at, CancellationToken cancellationToken) =>
        ResolveActiveAlertsAsync([], assetIds, reason, at, cancellationToken);

    /// <summary>
    /// Ends the open Attachments matching <paramref name="which"/> at <paramref name="at"/>. Detach and both Archives end
    /// Attachments only through here, so each also resolves the Active Alerts of the Assets that lost their Device as
    /// Device detached and forgets those Assets' rule state, so the next Device starts each rule afresh (#34). Call
    /// <see cref="BumpAlertRuleVersionAsync"/> first. Returns the resolved Alerts' ids for the live push once the caller
    /// commits.
    /// </summary>
    public async Task<List<Guid>> EndOpenAttachmentsAsync(Expression<Func<Attachment, bool>> which, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var open = Attachments.Where(which).Where(attachment => attachment.DetachedAt == null);
        Guid[] assetIds = [.. await open.Select(attachment => attachment.AssetId).ToListAsync(cancellationToken)];
        await open.ExecuteUpdateAsync(set => set.SetProperty(attachment => attachment.DetachedAt, at), cancellationToken);
        await AlertRuleStates.Where(state => assetIds.Contains(state.AssetId)).ExecuteDeleteAsync(cancellationToken);
        return await ResolveActiveAlertsOfAssetsAsync(assetIds, ResolveReason.DeviceDetached, at, cancellationToken);
    }

    // One UPDATE ... RETURNING, so the ids are exactly the Alerts it resolved; conditional like every resolve, and
    // resolved_by stays null (a platform resolve).
    private Task<List<Guid>> ResolveActiveAlertsAsync(
        Guid[] ruleIds, Guid[] assetIds, ResolveReason reason, DateTimeOffset at, CancellationToken cancellationToken) =>
        Database.SqlQuery<Guid>($"""
            UPDATE alerts SET status = {nameof(AlertStatus.Resolved)}, resolved_at = {at}, resolve_reason = {reason.ToString()}
            WHERE (alert_rule_id = ANY({ruleIds}) OR asset_id = ANY({assetIds})) AND status <> {nameof(AlertStatus.Resolved)}
            RETURNING id AS "Value"
            """).ToListAsync(cancellationToken);

    /// <summary>
    /// Creates the missing daily Reading partitions for the UTC days <paramref name="first"/> to <paramref name="last"/>
    /// (ADR 0003: own job, no pg_partman). Safe to run from several hosts at once.
    /// </summary>
    public Task CreateReadingPartitionsAsync(DateOnly first, DateOnly last, CancellationToken cancellationToken)
    {
        // One implicit transaction; the advisory lock makes concurrent runs take turns instead of racing on the names.
        // CREATE ... PARTITION OF takes ACCESS EXCLUSIVE on readings, and ingest would queue behind a CREATE waiting
        // on a long reader, so give up after 5 s: partitions exist days ahead and the job retries next hour.
        var sql = new StringBuilder("SELECT pg_advisory_xact_lock(hashtext('readings partitions'));SET LOCAL lock_timeout = '5s';");
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
            user.Property(u => u.DisplayName).HasMaxLength(FieldTrackUser.MaxDisplayNameLength);
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
            asset.ToTable(table => table.HasCheckConstraint("ck_assets_type", NamesCheck<AssetType>("type")));
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

        // Enums are stored by name with checks, like Asset Type, because ingest reads rules and writes Alerts with
        // hand-written SQL (ADR 0005). Enum names in SQL come from nameof, so a rename reaches every check.
        builder.Entity<AlertRule>(rule =>
        {
            rule.Property(r => r.Kind).HasConversion<string>();
            rule.Property(r => r.Severity).HasConversion<string>();
            // Plain column names; ThresholdSettings refuses bad values in C#, the checks below in SQL.
            rule.ComplexProperty(r => r.ThresholdSettings, settings =>
            {
                settings.Property(s => s.Sensor).HasColumnName("sensor").HasConversion<string>();
                settings.Property(s => s.Operator).HasColumnName("operator").HasConversion<string>();
                settings.Property(s => s.Threshold).HasColumnName("threshold");
                settings.Property(s => s.Hysteresis).HasColumnName("hysteresis");
            });
            rule.ToTable(table =>
            {
                table.HasCheckConstraint("ck_alert_rules_kind", NamesCheck<AlertRuleKind>("kind"));
                table.HasCheckConstraint("ck_alert_rules_severity", NamesCheck<Severity>("severity"));
                table.HasCheckConstraint("ck_alert_rules_sensor", NamesCheck<Sensor>("sensor"));
                table.HasCheckConstraint("ck_alert_rules_operator", NamesCheck<ThresholdOperator>("operator"));
                table.HasCheckConstraint("ck_alert_rules_target", "(asset_id IS NULL) <> (asset_group_id IS NULL)");
                // double precision holds NaN and ±Infinity; Postgres sorts NaN above Infinity, so the upper bound on
                // hysteresis refuses both.
                table.HasCheckConstraint("ck_alert_rules_threshold",
                    $"kind <> '{nameof(AlertRuleKind.Threshold)}' OR (sensor IS NOT NULL AND operator IS NOT NULL"
                    + " AND threshold IS NOT NULL AND threshold NOT IN ('NaN', 'Infinity', '-Infinity')"
                    + " AND hysteresis IS NOT NULL AND hysteresis >= 0 AND hysteresis < 'Infinity')");
                // Only Offline rules have seconds, and they wait at least one 1 s sweep. The NULL case is spelled out
                // rather than left to a CHECK passing on NULL.
                table.HasCheckConstraint("ck_alert_rules_offline",
                    $"(kind = '{nameof(AlertRuleKind.Offline)}') = (offline_seconds IS NOT NULL) AND (offline_seconds IS NULL OR offline_seconds > 0)");
            });
            // A rule on one Asset goes with it when the Asset is deleted outright, which happens only while it has no
            // Readings and so no Alerts; an Asset with history is Archived instead (#24).
            rule.HasOne<Asset>().WithMany().HasForeignKey(r => r.AssetId).OnDelete(DeleteBehavior.Cascade);
            rule.HasOne<AssetGroup>().WithMany().HasForeignKey(r => r.AssetGroupId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Alert>(alert =>
        {
            // Optional: an Offline Alert has no Reading (silence raised it). Same column names as when it was required.
            alert.ComplexProperty(a => a.Reading, reading =>
            {
                reading.Property(r => r.DeviceId).HasColumnName("reading_device_id");
                reading.Property(r => r.Boot).HasColumnName("reading_boot");
                reading.Property(r => r.SequenceNumber).HasColumnName("reading_sequence_number");
            });
            alert.Property(a => a.Severity).HasConversion<string>();
            alert.Property(a => a.Status).HasConversion<string>();
            alert.Property(a => a.ResolveReason).HasConversion<string>();
            alert.ToTable(table =>
            {
                table.HasCheckConstraint("ck_alerts_severity", NamesCheck<Severity>("severity"));
                table.HasCheckConstraint("ck_alerts_status", NamesCheck<AlertStatus>("status"));
                table.HasCheckConstraint("ck_alerts_resolve_reason", NamesCheck<ResolveReason>("resolve_reason"));
                // Ingest and manual resolve write these with SQL: a Resolved Alert has its time and reason, an Active
                // one neither, and only a breach that cleared inside its batch is from backlog.
                table.HasCheckConstraint("ck_alerts_resolution",
                    $"(status = '{nameof(AlertStatus.Resolved)}') = (resolved_at IS NOT NULL)"
                    + " AND (resolved_at IS NULL) = (resolve_reason IS NULL)"
                    + $" AND (NOT from_backlog OR resolve_reason IS NOT DISTINCT FROM '{nameof(ResolveReason.Cleared)}')");
                // Acknowledge records who and when together; an Open Alert has neither, an Acknowledged one both.
                table.HasCheckConstraint("ck_alerts_status_acknowledged",
                    "(acknowledged_by IS NULL) = (acknowledged_at IS NULL)"
                    + $" AND (status <> '{nameof(AlertStatus.Open)}' OR acknowledged_at IS NULL)"
                    + $" AND (status <> '{nameof(AlertStatus.Acknowledged)}' OR acknowledged_at IS NOT NULL)");
                // Only a user Resolving by hand leaves their id, a note or a latch; the platform leaves none (#10).
                // The triggering Reading is whole or absent (Offline Alerts).
                table.HasCheckConstraint("ck_alerts_reading",
                    "(reading_device_id IS NULL) = (reading_boot IS NULL) AND (reading_boot IS NULL) = (reading_sequence_number IS NULL)");
                table.HasCheckConstraint("ck_alerts_resolved_by",
                    $"(resolved_by IS NOT NULL) = (resolve_reason IS NOT DISTINCT FROM '{nameof(ResolveReason.Manual)}')"
                    + " AND (resolved_by IS NOT NULL OR (note IS NULL AND NOT latched))");
            });
            alert.Property(a => a.Note).HasMaxLength(Alert.MaxNoteLength);
            // Users are never deleted, so the audit trail always names someone.
            alert.HasOne<FieldTrackUser>().WithMany().HasForeignKey(a => a.AcknowledgedBy).OnDelete(DeleteBehavior.Restrict);
            alert.HasOne<FieldTrackUser>().WithMany().HasForeignKey(a => a.ResolvedBy).OnDelete(DeleteBehavior.Restrict);
            // Alerts are history: their rule and Asset are kept (disabled, Archived), never deleted under them.
            alert.HasOne<AlertRule>().WithMany().HasForeignKey(a => a.AlertRuleId).OnDelete(DeleteBehavior.Restrict);
            alert.HasOne<Asset>().WithMany().HasForeignKey(a => a.AssetId).OnDelete(DeleteBehavior.Restrict);
            // At most one Active Alert per Alert Rule and Asset (#10, ADR 0005).
            alert.HasIndex(a => new { a.AlertRuleId, a.AssetId })
                .IsUnique()
                .HasFilter($"status <> '{nameof(AlertStatus.Resolved)}'")
                .HasDatabaseName("ix_alerts_alert_rule_id_asset_id_active");
            // The partial index above cannot serve a rule's whole history: the delete check and its foreign key use this.
            alert.HasIndex(a => a.AlertRuleId);
            // The Alert feed, newest first, and an Asset's Alert history.
            alert.HasIndex(a => a.RaisedAt);
            alert.HasIndex(a => new { a.AssetId, a.RaisedAt });
        });

        // Rule state is not history: it goes with its rule or Asset.
        builder.Entity<AlertRuleState>(state =>
        {
            state.HasKey(s => new { s.AlertRuleId, s.AssetId });
            // The last evaluated Reading is whole or absent: an Offline rule's sweep can set state before any Reading.
            state.ToTable(table => table.HasCheckConstraint("ck_alert_rule_states_last_evaluated",
                "(last_device_id IS NULL) = (last_boot IS NULL) AND (last_boot IS NULL) = (last_sequence_number IS NULL)"));
            state.HasOne<AlertRule>().WithMany().HasForeignKey(s => s.AlertRuleId).OnDelete(DeleteBehavior.Cascade);
            state.HasOne<Asset>().WithMany().HasForeignKey(s => s.AssetId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<RejectedMessage>(rejected =>
        {
            // By name, like Asset Type, and checked so hand-written SQL keeps to the same names.
            rejected.Property(r => r.Reason).HasConversion<string>();
            rejected.ToTable(table => table.HasCheckConstraint("ck_rejected_messages_reason", NamesCheck<RejectReason>("reason")));
            // Anyone who can publish can add rows, so each row is bounded: the payload by the broker's
            // message_size_limit, the topic here.
            rejected.Property(r => r.Topic).HasMaxLength(RejectedMessage.MaxTopicLength);
            // The retention purge deletes by Received time; the second index finds a topic's recent Rejected messages.
            rejected.HasIndex(r => r.ReceivedTime);
            rejected.HasIndex(r => new { r.Topic, r.ReceivedTime });
        });
    }

    /// <summary>A CHECK that <paramref name="column"/> holds the name of a <typeparamref name="TEnum"/> member.</summary>
    private static string NamesCheck<TEnum>(string column) where TEnum : struct, Enum =>
        $"{column} IN ({string.Join(", ", Enum.GetNames<TEnum>().Select(name => $"'{name}'"))})";
}
