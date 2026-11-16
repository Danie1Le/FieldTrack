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
}
