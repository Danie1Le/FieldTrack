using Npgsql;

namespace FieldTrack.IntegrationTests;

public sealed class MigrationTests(FieldTrackFixture fixture) : IntegrationTest(fixture)
{
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
        await ExecuteAsync("""
            INSERT INTO devices (id, serial_number, archived) VALUES ('00000000-0000-0000-0000-000000000001', 'd1', false);
            INSERT INTO assets (id, name, type, archived) VALUES ('00000000-0000-0000-0000-000000000002', 'a1', 'Truck', false)
            """);

        var failure = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync("""
            INSERT INTO attachments (id, device_id, asset_id, attached_at, detached_at)
            VALUES (gen_random_uuid(), '00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000002',
                    '2026-01-02T00:00:00Z', '2026-01-01T00:00:00Z')
            """));

        failure.ConstraintName.ShouldBe("ck_attachments_period");
    }

    [Theory]
    [InlineData("NULL, NULL")]
    [InlineData("'00000000-0000-0000-0000-000000000002', '00000000-0000-0000-0000-000000000003'")]
    public async Task Database_refuses_an_Alert_Rule_that_does_not_target_exactly_one_Asset_or_Asset_Group(string targets)
    {
        await AssetAndGroupAsync();

        var failure = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync($"""
            INSERT INTO alert_rules (id, name, kind, asset_id, asset_group_id, severity, enabled, sensor, operator, threshold, hysteresis)
            VALUES (gen_random_uuid(), 'r', 'Threshold', {targets}, 'Critical', true, 'EngineTempC', 'GreaterThan', 105, 5)
            """));

        failure.ConstraintName.ShouldBe("ck_alert_rules_target");
    }

    [Theory]
    [InlineData("NULL, 'GreaterThan', 105, 5")]
    [InlineData("'EngineTempC', NULL, 105, 5")]
    [InlineData("'EngineTempC', 'GreaterThan', NULL, 5")]
    [InlineData("'EngineTempC', 'GreaterThan', 105, NULL")]
    [InlineData("'EngineTempC', 'GreaterThan', 105, -1")]
    public async Task Database_refuses_a_Threshold_rule_without_its_settings_or_with_negative_Hysteresis(string settings)
    {
        await AssetAndGroupAsync();

        var failure = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync($"""
            INSERT INTO alert_rules (id, name, kind, asset_id, severity, enabled, sensor, operator, threshold, hysteresis)
            VALUES (gen_random_uuid(), 'r', 'Threshold', '00000000-0000-0000-0000-000000000002', 'Critical', true, {settings})
            """));

        failure.ConstraintName.ShouldBe("ck_alert_rules_threshold");
    }

    [Fact]
    public async Task Database_allows_one_Active_Alert_per_Alert_Rule_and_Asset_besides_any_number_of_Resolved_ones()
    {
        await AssetAndGroupAsync();
        await ExecuteAsync("""
            INSERT INTO alert_rules (id, name, kind, asset_id, severity, enabled, sensor, operator, threshold, hysteresis)
            VALUES ('00000000-0000-0000-0000-000000000004', 'r', 'Threshold', '00000000-0000-0000-0000-000000000002', 'Critical', true,
                    'EngineTempC', 'GreaterThan', 105, 5)
            """);
        await ExecuteAsync($"{InsertAlert("Resolved")};{InsertAlert("Resolved")};{InsertAlert("Open")}");

        var failure = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(InsertAlert("Acknowledged")));

        failure.ConstraintName.ShouldBe("ix_alerts_alert_rule_id_asset_id_active");
    }

    private static string InsertAlert(string status) => $"""
        INSERT INTO alerts (id, alert_rule_id, asset_id, reading_device_id, reading_boot, reading_sequence_number, severity, message,
                            status, raised_at, created_at, from_backlog)
        VALUES (gen_random_uuid(), '00000000-0000-0000-0000-000000000004', '00000000-0000-0000-0000-000000000002', gen_random_uuid(), 1, 0,
                'Critical', 'm', '{status}', now(), now(), false)
        """;

    private Task AssetAndGroupAsync() => ExecuteAsync("""
        INSERT INTO assets (id, name, type, archived) VALUES ('00000000-0000-0000-0000-000000000002', 'a1', 'Truck', false);
        INSERT INTO asset_groups (id, name) VALUES ('00000000-0000-0000-0000-000000000003', 'g1')
        """);

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Fixture.DatabaseConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(CancellationToken);
    }
}
