using FieldTrack.Domain.Alerts;
using FieldTrack.Domain.Telemetry;
using FieldTrack.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FieldTrack.IntegrationTests;

public sealed class MigrationTests(FieldTrackFixture fixture) : IntegrationTest(fixture)
{
    private const string AssetId = "00000000-0000-0000-0000-000000000002";
    private const string AssetGroupId = "00000000-0000-0000-0000-000000000003";
    private const string AlertRuleId = "00000000-0000-0000-0000-000000000004";
    private const string GeofenceId = "00000000-0000-0000-0000-000000000005";
    // The seeded Admin, for columns that name a user.
    private const string AnyUser = "(SELECT id FROM users LIMIT 1)";

    [Fact]
    public async Task Startup_in_development_creates_the_database_with_PostGIS_3_6()
    {
        await using var connection = new NpgsqlConnection(Fixture.DatabaseConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand("SELECT extversion FROM pg_extension WHERE extname = 'postgis'", connection);

        var version = (string?)await command.ExecuteScalarAsync(CancellationToken);

        // Spec #21 guard (PostGIS 3.6 everywhere), deliberately not derived from TestImages.
        version.ShouldNotBeNull().ShouldStartWith("3.6");
    }

    [Fact]
    public async Task Startup_outside_development_creates_and_migrates_the_database_when_MigrateOnStartup_is_set()
    {
        var database = $"fieldtrack_{Guid.NewGuid():N}";
        var connectionString = new NpgsqlConnectionStringBuilder(Fixture.DatabaseConnectionString) { Database = database }.ConnectionString;
        try
        {
            await using var api = OutsideDevelopment(connectionString, "true");
            api.StartServer();

            await using var scope = api.Services.CreateAsyncScope();
            var pending = await scope.ServiceProvider.GetRequiredService<FieldTrackDbContext>().Database.GetPendingMigrationsAsync(CancellationToken);

            pending.ShouldBeEmpty();
        }
        finally
        {
            await using var connection = new NpgsqlConnection(Fixture.DatabaseConnectionString);
            await connection.OpenAsync(CancellationToken);
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {database} WITH (FORCE)", connection);
            await drop.ExecuteNonQueryAsync(CancellationToken);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public async Task Startup_outside_development_leaves_the_schema_alone_unless_MigrateOnStartup_is_set(string? migrateOnStartup)
    {
        await WithEmptyDatabaseAsync(async connectionString =>
        {
            await using var api = OutsideDevelopment(connectionString, migrateOnStartup);

            // Unmigrated, the user seeder finds no tables.
            Should.Throw<PostgresException>(api.StartServer).SqlState.ShouldBe(PostgresErrorCodes.UndefinedTable);
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(CancellationToken);
            (await SqlAsync(connection, "SELECT count(*) FROM pg_tables WHERE schemaname = 'public'")).ShouldBe(0L);
        });
    }

    [Fact]
    public async Task Startup_stops_when_MigrateOnStartup_is_set_and_the_migration_fails()
    {
        await WithEmptyDatabaseAsync(async connectionString =>
        {
            // A history table EF cannot read fails the migration with UndefinedColumn. Carrying on past it would fail
            // differently, in the user seeder, with UndefinedTable.
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync(CancellationToken);
                await using var command = new NpgsqlCommand("CREATE TABLE \"__EFMigrationsHistory\" (unexpected int)", connection);
                await command.ExecuteNonQueryAsync(CancellationToken);
            }

            await using var api = OutsideDevelopment(connectionString, "true");

            Should.Throw<PostgresException>(api.StartServer).SqlState.ShouldBe(PostgresErrorCodes.UndefinedColumn);
        });
    }

    [Theory]
    [InlineData("INSERT INTO devices (id, serial_number, archived) VALUES (gen_random_uuid(), 'a/b', false)", "ck_devices_serial_number")]
    [InlineData("INSERT INTO devices (id, serial_number, archived) VALUES (gen_random_uuid(), '', false)", "ck_devices_serial_number")]
    [InlineData("INSERT INTO devices (id, serial_number, archived) VALUES (gen_random_uuid(), repeat('a', 65), false)", "ck_devices_serial_number")]
    [InlineData("INSERT INTO devices (id, serial_number, archived) VALUES (gen_random_uuid(), E'ok\n', false)", "ck_devices_serial_number")]
    public async Task Database_refuses_a_serial_number_that_is_not_one_topic_level(string sql, string constraint)
    {
        var failure = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(sql));

        failure.ConstraintName.ShouldBe(constraint);
    }

    [Fact]
    public async Task Database_refuses_an_Attachment_that_ends_before_it_starts()
    {
        await ExecuteAsync($"""
            INSERT INTO devices (id, serial_number, archived) VALUES ('00000000-0000-0000-0000-000000000001', 'd1', false);
            INSERT INTO assets (id, name, type, archived) VALUES ('{AssetId}', 'a1', 'Truck', false)
            """);

        var failure = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync($"""
            INSERT INTO attachments (id, device_id, asset_id, attached_at, detached_at)
            VALUES (gen_random_uuid(), '00000000-0000-0000-0000-000000000001', '{AssetId}',
                    '2026-01-02T00:00:00Z', '2026-01-01T00:00:00Z')
            """));

        failure.ConstraintName.ShouldBe("ck_attachments_period");
    }

    [Fact]
    public async Task Database_keeps_a_Rejected_message_by_reason_name_and_refuses_an_unknown_reason()
    {
        await ExecuteAsync("""
            INSERT INTO rejected_messages (topic, topic_clipped, payload, reason, received_time)
            VALUES ('fieldtrack/v2/devices/TRK-007/telemetry', false, '\x7b7d', 'UnknownTopicVersion', now())
            """);

        var failure = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync("""
            INSERT INTO rejected_messages (topic, topic_clipped, payload, reason, received_time)
            VALUES ('fieldtrack/v1/devices/TRK-007/telemetry', false, '\x7b7d', 'Unreadable', now())
            """));

        failure.ConstraintName.ShouldBe("ck_rejected_messages_reason");
    }

    [Fact]
    public async Task Database_refuses_a_Rejected_message_topic_longer_than_MaxTopicLength()
    {
        var failure = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync($"""
            INSERT INTO rejected_messages (topic, topic_clipped, payload, reason, received_time)
            VALUES (repeat('x', {RejectedMessage.MaxTopicLength + 1}), false, '\x7b7d', 'UnknownSerialNumber', now())
            """));

        failure.SqlState.ShouldBe(PostgresErrorCodes.StringDataRightTruncation);
    }

    [Theory]
    [InlineData("NULL", "NULL")]
    [InlineData($"'{AssetId}'", $"'{AssetGroupId}'")]
    public async Task Database_refuses_an_Alert_Rule_that_does_not_target_exactly_one_Asset_or_Asset_Group(string assetId, string assetGroupId)
    {
        await InsertAssetAndAssetGroupAsync();

        var failure = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(InsertRule(("asset_id", assetId), ("asset_group_id", assetGroupId))));

        failure.ConstraintName.ShouldBe("ck_alert_rules_target");
    }

    [Theory]
    [InlineData("sensor", "NULL")]
    [InlineData("operator", "NULL")]
    [InlineData("threshold", "NULL")]
    [InlineData("threshold", "'NaN'")]
    [InlineData("threshold", "'Infinity'")]
    [InlineData("threshold", "'-Infinity'")]
    [InlineData("hysteresis", "NULL")]
    [InlineData("hysteresis", "-1")]
    [InlineData("hysteresis", "'NaN'")]
    [InlineData("hysteresis", "'Infinity'")]
    public async Task Database_refuses_a_Threshold_rule_without_its_settings_or_with_a_non_finite_or_negative_number(string column, string value)
    {
        await InsertAssetAndAssetGroupAsync();

        var failure = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(InsertRule((column, value))));

        failure.ConstraintName.ShouldBe("ck_alert_rules_threshold");
    }

    [Theory]
    [InlineData("'Offline'", "NULL")]
    [InlineData("'Offline'", "0")]
    [InlineData("'Offline'", "-1")]
    [InlineData("'Threshold'", "60")]
    public async Task Database_refuses_an_Offline_rule_without_positive_seconds_and_seconds_on_another_kind(string kind, string offlineSeconds)
    {
        await InsertAssetAndAssetGroupAsync();

        var failure = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(InsertRule(("kind", kind), ("offline_seconds", offlineSeconds))));

        failure.ConstraintName.ShouldBe("ck_alert_rules_offline");
    }

    [Fact]
    public async Task Database_keeps_an_Offline_rule_and_its_Alert_and_rule_state_without_a_Reading()
    {
        await InsertAssetAndAssetGroupAsync();
        await ExecuteAsync(InsertRule(("kind", "'Offline'"), ("offline_seconds", "60"),
            ("sensor", "NULL"), ("operator", "NULL"), ("threshold", "NULL"), ("hysteresis", "NULL")));

        await ExecuteAsync(InsertAlert(("reading_device_id", "NULL"), ("reading_boot", "NULL"), ("reading_sequence_number", "NULL")));
        await ExecuteAsync(InsertState("NULL", "NULL", "NULL"));

        await using var connection = await OpenDatabaseAsync();
        (await SqlAsync(connection, "SELECT (SELECT count(*) FROM alerts) + (SELECT count(*) FROM alert_rule_states)")).ShouldBe(2L);
    }

    [Theory]
    [InlineData("reading_device_id")]
    [InlineData("reading_boot")]
    [InlineData("reading_sequence_number")]
    public async Task Database_refuses_an_Alert_with_part_of_a_Reading(string column)
    {
        await InsertRuleAsync();

        var failure = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(InsertAlert((column, "NULL"))));

        failure.ConstraintName.ShouldBe("ck_alerts_reading");
    }

    [Theory]
    [InlineData("NULL", "1", "0")]
    [InlineData("gen_random_uuid()", "NULL", "0")]
    [InlineData("gen_random_uuid()", "1", "NULL")]
    public async Task Database_refuses_rule_state_with_part_of_its_last_evaluated_Reading(string device, string boot, string sequenceNumber)
    {
        await InsertRuleAsync();

        var failure = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(InsertState(device, boot, sequenceNumber)));

        failure.ConstraintName.ShouldBe("ck_alert_rule_states_last_evaluated");
    }

    [Theory]
    [InlineData("'GeofenceExit'", "NULL")]
    [InlineData("'GeofenceEnter'", "NULL")]
    [InlineData("'Threshold'", $"'{GeofenceId}'")]
    public async Task Database_refuses_a_Geofence_rule_without_its_Geofence_and_any_other_kind_with_one(string kind, string geofenceId)
    {
        await InsertAssetAndAssetGroupAsync();
        await ExecuteAsync(InsertGeofence);

        var failure = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(InsertRule(("kind", kind), ("geofence_id", geofenceId))));

        failure.ConstraintName.ShouldBe("ck_alert_rules_geofence");
    }

    [Fact]
    public async Task Database_keeps_a_Geofence_area_as_a_geography_polygon_and_refuses_to_delete_one_a_rule_names()
    {
        await InsertAssetAndAssetGroupAsync();
        await ExecuteAsync(InsertGeofence);
        await ExecuteAsync(InsertRule(("kind", "'GeofenceExit'"), ("geofence_id", $"'{GeofenceId}'")));

        var failure = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync("DELETE FROM geofences"));

        failure.ConstraintName.ShouldBe("fk_alert_rules_geofences_geofence_id");
        await using var connection = await OpenDatabaseAsync();
        (await SqlAsync(connection, "SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'geofences'::regclass AND attname = 'area'"))
            .ShouldBe("geography(Polygon,4326)");
        (await SqlAsync(connection, "SELECT indexdef FROM pg_indexes WHERE tablename = 'geofences' AND indexdef LIKE '%(area)%'"))
            .ShouldBe("CREATE INDEX ix_geofences_area ON public.geofences USING gist (area)");
    }

    [Theory]
    [InlineData("geofences", "kind", "ck_geofences_kind")]
    [InlineData("alert_rules", "kind", "ck_alert_rules_kind")]
    [InlineData("alert_rules", "severity", "ck_alert_rules_severity")]
    [InlineData("alert_rules", "sensor", "ck_alert_rules_sensor")]
    [InlineData("alert_rules", "operator", "ck_alert_rules_operator")]
    [InlineData("alerts", "severity", "ck_alerts_severity")]
    [InlineData("alerts", "status", "ck_alerts_status")]
    public async Task Database_refuses_a_name_that_is_not_an_enum_member(string table, string column, string constraint)
    {
        await InsertRuleAsync();

        var failure = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(table switch
        {
            "alerts" => InsertAlert((column, "'Nope'")),
            "geofences" => InsertGeofence.Replace("'Inclusion'", "'Nope'", StringComparison.Ordinal),
            _ => InsertRule(("id", "gen_random_uuid()"), (column, "'Nope'")),
        }));

        failure.ConstraintName.ShouldBe(constraint);
    }

    [Fact]
    public async Task Database_refuses_a_resolve_reason_that_is_not_an_enum_member()
    {
        await InsertRuleAsync();

        var failure = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(Resolved(("resolve_reason", "'Nope'"))));

        failure.ConstraintName.ShouldBe("ck_alerts_resolve_reason");
    }

    [Theory]
    [InlineData("'Resolved'", "NULL", "NULL", false)]
    [InlineData("'Resolved'", "now()", "NULL", false)]
    [InlineData("'Open'", "now()", "'Cleared'", false)]
    [InlineData("'Open'", "NULL", "'Cleared'", false)]
    [InlineData("'Open'", "NULL", "NULL", true)]
    [InlineData("'Resolved'", "now()", "'RuleChanged'", true)]
    public async Task Database_refuses_an_Alert_whose_status_and_resolution_disagree(string status, string resolvedAt, string resolveReason, bool fromBacklog)
    {
        await InsertRuleAsync();

        var failure = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(InsertAlert(
            ("status", status), ("resolved_at", resolvedAt), ("resolve_reason", resolveReason), ("from_backlog", fromBacklog ? "true" : "false"))));

        failure.ConstraintName.ShouldBe("ck_alerts_resolution");
    }

    [Theory]
    [InlineData("'Open'", AnyUser, "now()")]
    [InlineData("'Acknowledged'", "NULL", "NULL")]
    [InlineData("'Acknowledged'", AnyUser, "NULL")]
    [InlineData("'Acknowledged'", "NULL", "now()")]
    public async Task Database_refuses_an_Alert_whose_status_and_acknowledgement_disagree(string status, string acknowledgedBy, string acknowledgedAt)
    {
        await InsertRuleAsync();

        var failure = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(InsertAlert(
            ("status", status), ("acknowledged_by", acknowledgedBy), ("acknowledged_at", acknowledgedAt))));

        failure.ConstraintName.ShouldBe("ck_alerts_status_acknowledged");
    }

    [Theory]
    [InlineData("'Manual'", "NULL", "NULL", false)]
    [InlineData("'Cleared'", AnyUser, "NULL", false)]
    [InlineData("'Cleared'", "NULL", "'n'", false)]
    [InlineData("'Cleared'", "NULL", "NULL", true)]
    public async Task Database_refuses_a_resolving_user_note_or_latch_on_an_Alert_not_Resolved_by_hand(
        string resolveReason, string resolvedBy, string note, bool latched)
    {
        await InsertRuleAsync();

        var failure = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(Resolved(
            ("resolve_reason", resolveReason), ("resolved_by", resolvedBy), ("note", note), ("latched", latched ? "true" : "false"))));

        failure.ConstraintName.ShouldBe("ck_alerts_resolved_by");
    }

    [Fact]
    public async Task Database_keeps_an_Alert_Resolved_by_hand_with_its_user_note_and_latch_and_refuses_a_note_longer_than_MaxNoteLength()
    {
        await InsertRuleAsync();
        // Acknowledged first: Resolving keeps who Acknowledged it (story 47).
        (string, string)[] byHand = [
            ("resolve_reason", "'Manual'"), ("resolved_by", AnyUser), ("latched", "true"), ("acknowledged_by", AnyUser), ("acknowledged_at", "now()")];
        await ExecuteAsync(Resolved([.. byHand, ("note", $"repeat('n', {Alert.MaxNoteLength})")]));

        var failure = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(Resolved([.. byHand, ("note", $"repeat('n', {Alert.MaxNoteLength + 1})")])));

        failure.SqlState.ShouldBe(PostgresErrorCodes.StringDataRightTruncation);
    }

    [Fact]
    public async Task AlertActions_gives_users_created_before_it_their_emails_local_part_as_display_name()
    {
        var database = $"backfill_{Guid.NewGuid():N}";
        await ExecuteAsync($"CREATE DATABASE {database}");
        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(Fixture.DatabaseConnectionString) { Database = database }.ConnectionString;
            await using var db = new FieldTrackDbContext(new DbContextOptionsBuilder<FieldTrackDbContext>()
                .UseNpgsql(connectionString, npgsql => npgsql.UseNetTopologySuite()).UseSnakeCaseNamingConvention().Options);
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("AlertRulesAndAlerts", CancellationToken);
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO users (id, user_name, email, email_confirmed, phone_number_confirmed, two_factor_enabled, lockout_enabled, access_failed_count, must_change_password)
                VALUES (gen_random_uuid(), 'jo.smith@example.com', 'jo.smith@example.com', false, false, false, true, 0, false)
                """, CancellationToken);

            await migrator.MigrateAsync(cancellationToken: CancellationToken);

            (await db.Database.SqlQueryRaw<string>("SELECT display_name AS \"Value\" FROM users").SingleAsync(CancellationToken)).ShouldBe("jo.smith");
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAsync($"DROP DATABASE {database} WITH (FORCE)");
        }
    }

    [Fact]
    public async Task DemoOperatorHardening_marks_the_seeded_Demo_Operator_and_gives_older_refresh_tokens_one_family_per_user()
    {
        var database = $"backfill_{Guid.NewGuid():N}";
        await ExecuteAsync($"CREATE DATABASE {database}");
        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(Fixture.DatabaseConnectionString) { Database = database }.ConnectionString;
            await using var db = new FieldTrackDbContext(new DbContextOptionsBuilder<FieldTrackDbContext>()
                .UseNpgsql(connectionString, npgsql => npgsql.UseNetTopologySuite()).UseSnakeCaseNamingConvention().Options);
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("ExportTickets", CancellationToken);
            // The seeder's Operator has lockout off, and keeps its email's local part as display name when no
            // demo-mode startup has run since display names existed. An Admin-created Operator has lockout on.
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO roles (id, name, normalized_name) VALUES ('00000000-0000-0000-0000-0000000000a1', 'Operator', 'OPERATOR');
                INSERT INTO users (id, user_name, email, display_name, email_confirmed, phone_number_confirmed, two_factor_enabled, lockout_enabled, access_failed_count, must_change_password)
                VALUES ('00000000-0000-0000-0000-00000000000d', 'demo@example.com', 'demo@example.com', 'demo', false, false, false, false, 0, false),
                       ('00000000-0000-0000-0000-00000000000e', 'jo@example.com', 'jo@example.com', 'Demo Operator', false, false, false, true, 0, false);
                INSERT INTO user_roles (user_id, role_id)
                VALUES ('00000000-0000-0000-0000-00000000000d', '00000000-0000-0000-0000-0000000000a1'),
                       ('00000000-0000-0000-0000-00000000000e', '00000000-0000-0000-0000-0000000000a1');
                INSERT INTO refresh_tokens (id, user_id, token_hash, created_at, expires_at)
                VALUES (gen_random_uuid(), '00000000-0000-0000-0000-00000000000d', '\x01', now(), now() + interval '7 days'),
                       (gen_random_uuid(), '00000000-0000-0000-0000-00000000000d', '\x02', now(), now() + interval '7 days')
                """, CancellationToken);

            await migrator.MigrateAsync(cancellationToken: CancellationToken);

            (await db.Database.SqlQueryRaw<string>("SELECT email AS \"Value\" FROM users WHERE is_demo_operator").SingleAsync(CancellationToken)).ShouldBe("demo@example.com");
            (await db.Database.SqlQueryRaw<Guid>("SELECT DISTINCT family_id AS \"Value\" FROM refresh_tokens").SingleAsync(CancellationToken))
                .ShouldBe(Guid.Parse("00000000-0000-0000-0000-00000000000d"));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAsync($"DROP DATABASE {database} WITH (FORCE)");
        }
    }

    [Fact]
    public async Task Database_gives_each_refresh_token_written_without_a_family_a_family_of_its_own()
    {
        // While a deploy rolls out, the previous revision still writes refresh tokens without a family.
        await ExecuteAsync("""
            INSERT INTO users (id, user_name, email, display_name, email_confirmed, phone_number_confirmed, two_factor_enabled, lockout_enabled, access_failed_count, must_change_password)
            VALUES ('00000000-0000-0000-0000-00000000000d', 'a@example.com', 'a@example.com', 'A', false, false, false, true, 0, false),
                   ('00000000-0000-0000-0000-00000000000e', 'b@example.com', 'b@example.com', 'B', false, false, false, true, 0, false);
            INSERT INTO refresh_tokens (id, user_id, token_hash, created_at, expires_at)
            VALUES (gen_random_uuid(), '00000000-0000-0000-0000-00000000000d', '\x01', now(), now() + interval '7 days'),
                   (gen_random_uuid(), '00000000-0000-0000-0000-00000000000e', '\x02', now(), now() + interval '7 days')
            """);

        await using var connection = await OpenDatabaseAsync();
        (await SqlAsync(connection, "SELECT count(DISTINCT family_id) FROM refresh_tokens WHERE family_id <> '00000000-0000-0000-0000-000000000000'")).ShouldBe(2L);
    }

    [Fact]
    public async Task Database_allows_one_Active_Alert_per_Alert_Rule_and_Asset_besides_any_number_of_Resolved_ones()
    {
        await InsertRuleAsync();
        await ExecuteAsync($"{Resolved()};{Resolved(("from_backlog", "true"))};{InsertAlert()}");

        var failure = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(InsertAlert(
            ("status", "'Acknowledged'"), ("acknowledged_by", AnyUser), ("acknowledged_at", "now()"))));

        failure.ConstraintName.ShouldBe("ix_alerts_alert_rule_id_asset_id_active");
    }

    [Fact]
    public async Task Database_allows_a_new_Active_Alert_once_the_previous_one_is_Resolved()
    {
        await InsertRuleAsync();
        await ExecuteAsync(InsertAlert());
        await ExecuteAsync("UPDATE alerts SET status = 'Resolved', resolved_at = now(), resolve_reason = 'Cleared'");

        await ExecuteAsync(InsertAlert());

        await using var connection = await OpenDatabaseAsync();
        (await SqlAsync(connection, "SELECT count(*) FROM alerts WHERE status = 'Open'")).ShouldBe(1L);
    }

    [Fact]
    public async Task Database_keeps_Alerts_by_refusing_to_delete_their_Alert_Rule_or_Asset()
    {
        // A group rule, so deleting the Asset is refused by the Alert itself and not by the rule cascading.
        await InsertAssetAndAssetGroupAsync();
        await ExecuteAsync($"UPDATE assets SET asset_group_id = '{AssetGroupId}'");
        await ExecuteAsync(InsertRule(("asset_id", "NULL"), ("asset_group_id", $"'{AssetGroupId}'")));
        await ExecuteAsync(InsertAlert());

        var assetDelete = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync("DELETE FROM assets"));
        var ruleDelete = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync("DELETE FROM alert_rules"));

        assetDelete.ConstraintName.ShouldBe("fk_alerts_assets_asset_id");
        ruleDelete.ConstraintName.ShouldBe("fk_alerts_alert_rules_alert_rule_id");
    }

    [Fact]
    public async Task Deleting_an_Asset_without_Alerts_deletes_its_Alert_Rules_and_rule_state()
    {
        await InsertRuleAsync();
        await ExecuteAsync(InsertState("gen_random_uuid()", "1", "0"));

        await ExecuteAsync("DELETE FROM assets");

        await using var connection = await OpenDatabaseAsync();
        (await SqlAsync(connection, "SELECT (SELECT count(*) FROM alert_rules) + (SELECT count(*) FROM alert_rule_states)")).ShouldBe(0L);
    }

    // A null migrateOnStartup leaves the key out (UseSetting would set it to an empty string).
    private WebApplicationFactory<Program> OutsideDevelopment(string connectionString, string? migrateOnStartup) =>
        Fixture.Api.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production")
                .UseSetting($"ConnectionStrings:{PersistenceServiceCollectionExtensions.ConnectionStringName}", connectionString);
            if (migrateOnStartup is not null)
            {
                builder.UseSetting(Program.MigrateOnStartupKey, migrateOnStartup);
            }
        });

    private async Task WithEmptyDatabaseAsync(Func<string, Task> test)
    {
        var database = $"empty_{Guid.NewGuid():N}";
        await ExecuteAsync($"CREATE DATABASE {database}");
        try
        {
            await test(new NpgsqlConnectionStringBuilder(Fixture.DatabaseConnectionString) { Database = database }.ConnectionString);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAsync($"DROP DATABASE {database} WITH (FORCE)");
        }
    }

    // Each insert starts from a valid row; tests override single columns with SQL literals.
    private static string Insert(string table, Dictionary<string, string> values, (string Column, string Value)[] overrides)
    {
        foreach (var (column, value) in overrides)
        {
            values[column] = value;
        }

        return $"INSERT INTO {table} ({string.Join(", ", values.Keys)}) VALUES ({string.Join(", ", values.Values)})";
    }

    private static string InsertRule(params (string Column, string Value)[] overrides) => Insert("alert_rules", new()
    {
        ["id"] = $"'{AlertRuleId}'",
        ["name"] = "'r'",
        ["kind"] = "'Threshold'",
        ["asset_id"] = $"'{AssetId}'",
        ["asset_group_id"] = "NULL",
        ["severity"] = "'Critical'",
        ["enabled"] = "true",
        ["sensor"] = "'EngineTempC'",
        ["operator"] = "'GreaterThan'",
        ["threshold"] = "105",
        ["hysteresis"] = "5",
    }, overrides);

    private const string InsertGeofence = $"""
        INSERT INTO geofences (id, name, kind, area)
        VALUES ('{GeofenceId}', 'Depot', 'Inclusion', 'SRID=4326;POLYGON((0 0, 1 0, 1 1, 0 1, 0 0))')
        """;

    private static string InsertAlert(params (string Column, string Value)[] overrides) => Insert("alerts", new()
    {
        ["id"] = "gen_random_uuid()",
        ["alert_rule_id"] = $"'{AlertRuleId}'",
        ["asset_id"] = $"'{AssetId}'",
        ["reading_device_id"] = "gen_random_uuid()",
        ["reading_boot"] = "1",
        ["reading_sequence_number"] = "0",
        ["severity"] = "'Critical'",
        ["message"] = "'m'",
        ["status"] = "'Open'",
        ["raised_at"] = "now()",
        ["resolved_at"] = "NULL",
        ["resolve_reason"] = "NULL",
        ["created_at"] = "now()",
        ["from_backlog"] = "false",
    }, overrides);

    private static string InsertState(string lastDeviceId, string lastBoot, string lastSequenceNumber) => $"""
        INSERT INTO alert_rule_states (alert_rule_id, asset_id, breached, last_device_id, last_boot, last_sequence_number)
        VALUES ('{AlertRuleId}', '{AssetId}', true, {lastDeviceId}, {lastBoot}, {lastSequenceNumber})
        """;

    private static string Resolved(params (string Column, string Value)[] overrides) =>
        InsertAlert([("status", "'Resolved'"), ("resolved_at", "now()"), ("resolve_reason", "'Cleared'"), .. overrides]);

    private async Task InsertRuleAsync()
    {
        await InsertAssetAndAssetGroupAsync();
        await ExecuteAsync(InsertRule());
    }

    private Task InsertAssetAndAssetGroupAsync() => ExecuteAsync($"""
        INSERT INTO assets (id, name, type, archived) VALUES ('{AssetId}', 'a1', 'Truck', false);
        INSERT INTO asset_groups (id, name) VALUES ('{AssetGroupId}', 'g1')
        """);

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Fixture.DatabaseConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(CancellationToken);
    }
}
