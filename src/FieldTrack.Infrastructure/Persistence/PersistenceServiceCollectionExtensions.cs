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

    /// <summary>
    /// Adds <see cref="FieldTrackDbContext"/> on Npgsql with NetTopologySuite spatial types and
    /// snake_case table and column names, the names the hand-written SQL beside EF uses (ADR 0003, ADR 0004),
    /// the <see cref="IReadingHistory"/> that Asset and Device delete ask, and an <see cref="NpgsqlDataSource"/>
    /// for the ingest writer's own connection (ADR 0004).
    /// </summary>
    public static IServiceCollection AddFieldTrackDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is not configured.");

        return services
            .AddScoped<IReadingHistory, ReadingHistory>()
            .AddSingleton(_ => NpgsqlDataSource.Create(connectionString))
            .AddDbContext<FieldTrackDbContext>(options =>
                options.UseNpgsql(connectionString, npgsql => npgsql.UseNetTopologySuite()).UseSnakeCaseNamingConvention());
    }
}
