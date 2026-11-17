using Npgsql;
using Respawn;

namespace FieldTrack.IntegrationTests;

public sealed class DatabaseResetTests(FieldTrackFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Reset_deletes_test_rows_and_keeps_the_PostGIS_catalogue_and_migration_history()
    {
        await using var connection = new NpgsqlConnection(Fixture.DatabaseConnectionString);
        await connection.OpenAsync(CancellationToken);
        // A table of the test's own, so the test does not depend on which tables the migrations create.
        await ExecuteAsync(connection, "CREATE TABLE reset_probe (id int); INSERT INTO reset_probe VALUES (1)");
        try
        {
            var respawner = await Respawner.CreateAsync(connection, FieldTrackFixture.RespawnerOptions);

            await respawner.ResetAsync(connection);

            (await CountAsync(connection, "reset_probe")).ShouldBe(0);
            (await CountAsync(connection, "spatial_ref_sys")).ShouldBeGreaterThan(0);
            (await CountAsync(connection, "\"__EFMigrationsHistory\"")).ShouldBeGreaterThan(0);
        }
        finally
        {
            await ExecuteAsync(connection, "DROP TABLE reset_probe");
        }
    }

    [Fact]
    public async Task Reset_keeps_the_one_Alert_Rule_version_row()
    {
        await using var connection = await OpenDatabaseAsync();

        // IntegrationTest has reset the database before this test.
        (await CountAsync(connection, "alert_rule_version")).ShouldBe(1);
    }

    [Theory]
    [InlineData("INSERT INTO alert_rule_version (version) VALUES (0)", PostgresErrorCodes.UniqueViolation)]
    [InlineData("INSERT INTO alert_rule_version (id, version) VALUES (false, 0)", PostgresErrorCodes.CheckViolation)]
    public async Task The_Alert_Rule_version_table_refuses_a_second_row(string insert, string sqlState)
    {
        await using var connection = await OpenDatabaseAsync();

        var refused = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(connection, insert));

        refused.SqlState.ShouldBe(sqlState);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(CancellationToken);
    }

    private static async Task<long> CountAsync(NpgsqlConnection connection, string table)
    {
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table}", connection);
        return (long)(await command.ExecuteScalarAsync(CancellationToken))!;
    }
}
