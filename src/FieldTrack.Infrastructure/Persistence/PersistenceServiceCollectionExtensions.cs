using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FieldTrack.Infrastructure.Persistence;

/// <summary>Registers FieldTrack's database access with a host.</summary>
public static class PersistenceServiceCollectionExtensions
{
    /// <summary>The connection string name read from configuration (<c>ConnectionStrings:FieldTrack</c>).</summary>
    public const string ConnectionStringName = "FieldTrack";

    /// <summary>The ingest writer's data source name, which Npgsql puts on its metrics and spans.</summary>
    public const string IngestDataSourceName = "fieldtrack-ingest";

    /// <summary>The EF Core data source name, which Npgsql puts on its metrics and spans.</summary>
    public const string EntityFrameworkDataSourceName = "fieldtrack-ef";

    /// <summary>
    /// Adds <see cref="FieldTrackDbContext"/> on Npgsql with NetTopologySuite spatial types and
    /// snake_case table and column names, the names the hand-written SQL beside EF uses (ADR 0003, ADR 0004),
    /// the <see cref="IReadingHistory"/> that Asset and Device delete ask, and an <see cref="NpgsqlDataSource"/>
    /// for the ingest writer's own connection (ADR 0004). Both data sources are named: Npgsql tags its telemetry with the
    /// name, which defaults to the connection string, so a name keeps the connection string out of telemetry (#48).
    /// </summary>
    public static IServiceCollection AddFieldTrackDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is not configured.");

        return services
            .AddScoped<IReadingHistory, ReadingHistory>()
            .AddSingleton(_ => new NpgsqlDataSourceBuilder(connectionString) { Name = IngestDataSourceName }.Build())
            .AddDbContext<FieldTrackDbContext>(options =>
                options.UseNpgsql(
                    connectionString,
                    npgsql => npgsql.UseNetTopologySuite().ConfigureDataSource(builder => builder.Name = EntityFrameworkDataSourceName)).UseSnakeCaseNamingConvention());
    }
}
