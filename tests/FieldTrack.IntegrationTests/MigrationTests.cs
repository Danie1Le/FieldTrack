using FieldTrack.Domain.Telemetry;
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

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Fixture.DatabaseConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(CancellationToken);
    }
}
