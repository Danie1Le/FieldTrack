using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FieldTrack.IntegrationTests;
using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(FieldTrackFixture))]

namespace FieldTrack.IntegrationTests;

/// <summary>
/// One PostGIS and one Mosquitto container for the whole test run, plus the API host started
/// against them. The shared host applies the migrations on startup in Development; hosts derived
/// from it with <c>WithWebHostBuilder</c> find nothing pending.
/// </summary>
public sealed class FieldTrackFixture : IAsyncLifetime
{
    private const ushort MqttPort = 1883;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder(TestImages.PostGis)
        // Same split as docker-compose: the image's init scripts load extensions into the
        // maintenance database, and the API creates and migrates the fieldtrack database itself.
        .WithDatabase("postgres")
        .Build();

    private readonly IContainer _broker = new ContainerBuilder(TestImages.Mosquitto)
        .WithPortBinding(MqttPort, assignRandomHostPort: true)
        .WithResourceMapping(
            new FileInfo(Path.Combine(AppContext.BaseDirectory, "mosquitto.conf")),
            new FileInfo("/mosquitto/config/mosquitto.conf"))
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged(
            "mosquitto version .+ running", wait => wait.WithTimeout(TimeSpan.FromMinutes(1))))
        .Build();

    /// <summary>The API host, running against the test containers in the Development environment.</summary>
    public FieldTrackApiFactory Api { get; private set; } = null!;

    /// <summary>Connection string for the database the API host migrates and uses.</summary>
    public string DatabaseConnectionString =>
        new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = "fieldtrack" }.ConnectionString;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _broker.StartAsync());

        Api = new FieldTrackApiFactory(DatabaseConnectionString, _broker.Hostname, _broker.GetMappedPublicPort(MqttPort));
        Api.StartServer();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Api.DisposeAsync();
        await _broker.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}
