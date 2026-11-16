using Microsoft.EntityFrameworkCore;

namespace FieldTrack.Infrastructure.Persistence;

/// <summary>
/// The EF Core unit of work over the FieldTrack PostgreSQL database. Migrations in this
/// assembly own the schema; the ingest write path (ADR 0004) uses hand-written SQL beside it.
/// </summary>
public sealed class FieldTrackDbContext(DbContextOptions<FieldTrackDbContext> options) : DbContext(options)
{
    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Positions and Geofence areas are PostGIS geography columns (ADR 0003, spec #21 Storage).
        modelBuilder.HasPostgresExtension("postgis");
    }
}
