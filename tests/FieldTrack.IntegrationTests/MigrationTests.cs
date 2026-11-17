using Npgsql;

namespace FieldTrack.IntegrationTests;

public sealed class MigrationTests(FieldTrackFixture fixture) : IntegrationTest(fixture)
{
    private const string AssetId = "00000000-0000-0000-0000-000000000002";
    private const string AssetGroupId = "00000000-0000-0000-0000-000000000003";
    private const string AlertRuleId = "00000000-0000-0000-0000-000000000004";

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
    [InlineData("alert_rules", "kind", "ck_alert_rules_kind")]
    [InlineData("alert_rules", "severity", "ck_alert_rules_severity")]
    [InlineData("alert_rules", "sensor", "ck_alert_rules_sensor")]
    [InlineData("alert_rules", "operator", "ck_alert_rules_operator")]
    [InlineData("alerts", "severity", "ck_alerts_severity")]
    [InlineData("alerts", "status", "ck_alerts_status")]
    public async Task Database_refuses_a_name_that_is_not_an_enum_member(string table, string column, string constraint)
    {
        await InsertRuleAsync();

        var failure = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(
            table == "alerts" ? InsertAlert((column, "'Nope'")) : InsertRule(("id", "gen_random_uuid()"), (column, "'Nope'"))));

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
    [InlineData("'Acknowledged'", "NULL", "'Manual'", false)]
    [InlineData("'Open'", "NULL", "NULL", true)]
    [InlineData("'Resolved'", "now()", "'Manual'", true)]
    public async Task Database_refuses_an_Alert_whose_status_and_resolution_disagree(string status, string resolvedAt, string resolveReason, bool fromBacklog)
    {
        await InsertRuleAsync();

        var failure = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(InsertAlert(
            ("status", status), ("resolved_at", resolvedAt), ("resolve_reason", resolveReason), ("from_backlog", fromBacklog ? "true" : "false"))));

        failure.ConstraintName.ShouldBe("ck_alerts_resolution");
    }

    [Fact]
    public async Task Database_allows_one_Active_Alert_per_Alert_Rule_and_Asset_besides_any_number_of_Resolved_ones()
    {
        await InsertRuleAsync();
        await ExecuteAsync($"{Resolved()};{Resolved(("from_backlog", "true"))};{InsertAlert()}");

        var failure = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(InsertAlert(("status", "'Acknowledged'"))));

        failure.ConstraintName.ShouldBe("ix_alerts_alert_rule_id_asset_id_active");
    }

    [Fact]
    public async Task Database_allows_a_new_Active_Alert_once_the_previous_one_is_Resolved()
    {
        await InsertRuleAsync();
        await ExecuteAsync(InsertAlert());
        await ExecuteAsync("UPDATE alerts SET status = 'Resolved', resolved_at = now(), resolve_reason = 'Manual'");

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
        await ExecuteAsync($"""
            INSERT INTO alert_rule_states (alert_rule_id, asset_id, breached, last_device_id, last_boot, last_sequence_number)
            VALUES ('{AlertRuleId}', '{AssetId}', false, gen_random_uuid(), 1, 0)
            """);

        await ExecuteAsync("DELETE FROM assets");

        await using var connection = await OpenDatabaseAsync();
        (await SqlAsync(connection, "SELECT (SELECT count(*) FROM alert_rules) + (SELECT count(*) FROM alert_rule_states)")).ShouldBe(0L);
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
        ["id"] = $"'{AlertRuleId}'", ["name"] = "'r'", ["kind"] = "'Threshold'", ["asset_id"] = $"'{AssetId}'", ["asset_group_id"] = "NULL",
        ["severity"] = "'Critical'", ["enabled"] = "true", ["sensor"] = "'EngineTempC'", ["operator"] = "'GreaterThan'",
        ["threshold"] = "105", ["hysteresis"] = "5",
    }, overrides);

    private static string InsertAlert(params (string Column, string Value)[] overrides) => Insert("alerts", new()
    {
        ["id"] = "gen_random_uuid()", ["alert_rule_id"] = $"'{AlertRuleId}'", ["asset_id"] = $"'{AssetId}'",
        ["reading_device_id"] = "gen_random_uuid()", ["reading_boot"] = "1", ["reading_sequence_number"] = "0",
        ["severity"] = "'Critical'", ["message"] = "'m'", ["status"] = "'Open'", ["raised_at"] = "now()", ["resolved_at"] = "NULL",
        ["resolve_reason"] = "NULL", ["created_at"] = "now()", ["from_backlog"] = "false",
    }, overrides);

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
