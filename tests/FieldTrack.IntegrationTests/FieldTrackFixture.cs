using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FieldTrack.IntegrationTests;
using Npgsql;
using Respawn;
using Respawn.Graph;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(FieldTrackFixture))]

namespace FieldTrack.IntegrationTests;

/// <summary>
/// One PostGIS and one Mosquitto container for the whole test run, plus the API host started
/// against them. The shared host applies the migrations on startup in Development; hosts derived
/// from it with <c>WithWebHostBuilder</c> find nothing pending. Tests reset data with Respawn
/// instead of recreating containers.
/// </summary>
public sealed class FieldTrackFixture : IAsyncLifetime
{
    private const ushort MqttPort = 1883;
    private const string Schema = "public";

    // PostGIS keeps its coordinate system catalogue in spatial_ref_sys; it is not test data.
    private static readonly string[] IgnoredTables = ["__EFMigrationsHistory", "spatial_ref_sys"];

    /// <summary>What a reset deletes: every table in the public schema except the ignored ones.</summary>
    internal static readonly RespawnerOptions RespawnerOptions = new()
    {
        DbAdapter = DbAdapter.Postgres,
        SchemasToInclude = [Schema],
        TablesToIgnore = [.. IgnoredTables.Select(table => new Table(table))],
    };

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder(TestImages.PostGis)
        // Same split as docker-compose: the image's init scripts load extensions into the
        // maintenance database, and the API creates and migrates the fieldtrack database itself.
        .WithDatabase("postgres")
        // The bench reads each batch's commit time (bench/docker-compose.bench.yml sets the same).
        .WithCommand("-c", "track_commit_timestamp=on")
        .Build();

    private readonly IContainer _broker = new ContainerBuilder(TestImages.Mosquitto)
        .WithPortBinding(MqttPort, assignRandomHostPort: true)
        // The shared config plus the one setting the bench needs (bench/docker-compose.bench.yml appends the same): $SYS counters every second.
        .WithResourceMapping(
            System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "mosquitto.conf")) + "\nsys_interval 1\n"),
            "/mosquitto/config/mosquitto.conf")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged(
            "mosquitto version .+ running", wait => wait.WithTimeout(TimeSpan.FromMinutes(1))))
        .Build();

    private Respawner? _respawner;

    /// <summary>The API host, running against the test containers in the Development environment.</summary>
    public FieldTrackApiFactory Api { get; private set; } = null!;

    /// <summary>Host name of the test broker.</summary>
    public string BrokerHost => _broker.Hostname;

    /// <summary>Port of the test broker on <see cref="BrokerHost"/>.</summary>
    public int BrokerPort => _broker.GetMappedPublicPort(MqttPort);

    /// <summary>Connection string for the database the API host migrates and uses.</summary>
    public string DatabaseConnectionString =>
        new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = "fieldtrack" }.ConnectionString;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _broker.StartAsync());

        Api = new FieldTrackApiFactory(DatabaseConnectionString, BrokerHost, BrokerPort);
        Api.StartServer();

        await using var connection = new NpgsqlConnection(DatabaseConnectionString);
        await connection.OpenAsync();

        // Respawn refuses a schema with no resettable tables; with none, there is nothing to reset.
        if (await CountResettableTablesAsync(connection) > 0)
        {
            _respawner = await Respawner.CreateAsync(connection, RespawnerOptions);
        }
    }

    /// <summary>Deletes all rows the tests wrote, leaving the schema and PostGIS catalogue in place.</summary>
    public async Task ResetDatabaseAsync()
    {
        if (_respawner is null)
        {
            return;
        }

        await using var connection = new NpgsqlConnection(DatabaseConnectionString);
        await connection.OpenAsync();
        await _respawner.ResetAsync(connection);
    }

    private static async Task<long> CountResettableTablesAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = @schema AND table_type = 'BASE TABLE' AND NOT (table_name = ANY(@ignored))",
            connection);
        command.Parameters.AddWithValue("schema", Schema);
        command.Parameters.AddWithValue("ignored", IgnoredTables);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Api.DisposeAsync();
        await _broker.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}
