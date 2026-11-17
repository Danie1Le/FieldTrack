using FieldTrack.Domain.Telemetry;
using FieldTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FieldTrack.Api.Maintenance;

/// <summary>
/// Housekeeping the database needs on a clock, run at startup and then hourly: daily Reading partitions
/// for every day ingest may write to (ADR 0003), and the purge of expired refresh tokens (ADR 0002).
/// </summary>
public sealed partial class DatabaseMaintenanceJob(IServiceScopeFactory scopes, TimeProvider time, ILogger<DatabaseMaintenanceJob> logger)
    : BackgroundService
{
    /// <summary>How many days ahead partitions exist (spec #21).</summary>
    public const int PartitionDaysAhead = 7;

    private static readonly TimeSpan _interval = TimeSpan.FromHours(1);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval, time);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Partitions exist days ahead, so one failed run is retried next hour without harm.
                LogFailed(exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FieldTrackDbContext>();
        var now = time.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        // Back as far as the oldest Device time ingest accepts, so a late Reading always has a partition.
        await db.CreateReadingPartitionsAsync(today.AddDays(-TelemetryPayload.MaxLate.Days), today.AddDays(PartitionDaysAhead), cancellationToken);

        // Only expired ones: a revoked token that has not expired must stay, because presenting it again
        // is how token theft is detected (ADR 0002).
        await db.RefreshTokens.Where(token => token.ExpiresAt < now).ExecuteDeleteAsync(cancellationToken);
    }

    [LoggerMessage(LogLevel.Error, "Database maintenance failed; retrying in an hour.")]
    private partial void LogFailed(Exception exception);
}
