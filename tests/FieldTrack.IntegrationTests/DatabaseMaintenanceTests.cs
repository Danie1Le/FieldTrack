using System.Globalization;
using FieldTrack.Infrastructure.Identity;
using FieldTrack.Infrastructure.Persistence;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FieldTrack.IntegrationTests;

public sealed class DatabaseMaintenanceTests(FieldTrackFixture fixture) : IntegrationTest(fixture)
{
    private static readonly TimeSpan _patience = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Startup_creates_daily_Reading_partitions_from_7_days_back_to_7_days_ahead()
    {
        // A clock far from the shared host's, so these partitions are made by this host alone.
        var time = new AdjustableTimeProvider { Offset = TimeSpan.FromDays(400) };
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var expected = Enumerable.Range(-7, 15).Select(day => PartitionName(today.AddDays(day))).ToList();

        await using var api = Fixture.Api.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(time)));
        api.StartServer();

        var partitions = await EventuallyAsync(
            () => ReadingPartitionsAsync(),
            names => expected.All(names.Contains));

        expected.ShouldBeSubsetOf(partitions);
        partitions.ShouldNotContain(PartitionName(today.AddDays(-8)));
        partitions.ShouldNotContain(PartitionName(today.AddDays(8)));
    }

    [Fact]
    public async Task A_Reading_partition_holds_one_UTC_day()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // The shared host's startup run makes it, and this test may run before that run commits.
        var bound = await EventuallyAsync(
            async () =>
            {
                await using var connection = await OpenAsync();
                await using var command = new NpgsqlCommand(
                    "SELECT pg_get_expr(c.relpartbound, c.oid) FROM pg_class c WHERE c.relname = @name", connection);
                command.Parameters.AddWithValue("name", PartitionName(today));
                return (string?)await command.ExecuteScalarAsync(CancellationToken);
            },
            bound => bound is not null);

        bound.ShouldBe($"FOR VALUES FROM ('{today:yyyy-MM-dd} 00:00:00+00') TO ('{today.AddDays(1):yyyy-MM-dd} 00:00:00+00')");
    }

    [Fact]
    public async Task Creating_a_partition_gives_up_rather_than_queue_ingest_behind_a_long_reader()
    {
        await using var reader = await OpenAsync();
        await using var transaction = await reader.BeginTransactionAsync(CancellationToken);
        await using (var lockReadings = new NpgsqlCommand("LOCK TABLE readings IN ACCESS SHARE MODE", reader, transaction))
        {
            await lockReadings.ExecuteNonQueryAsync(CancellationToken);
        }

        await using var scope = Fixture.Api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FieldTrackDbContext>();
        var day = new DateOnly(2100, 1, 1);

        var refused = await Should.ThrowAsync<PostgresException>(() => db.CreateReadingPartitionsAsync(day, day, CancellationToken));

        refused.SqlState.ShouldBe(PostgresErrorCodes.LockNotAvailable);
    }

    [Fact]
    public async Task Expired_refresh_tokens_are_purged_and_spent_ones_are_kept_one_extra_lifetime()
    {
        var now = DateTimeOffset.UtcNow;
        await using (var scope = Fixture.Api.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FieldTrackDbContext>();
            var user = await db.Users.SingleAsync(CancellationToken);
            db.RefreshTokens.AddRange(
                Token(user.Id, 1, expiresAt: now.AddMinutes(-1), revokedAt: null),
                Token(user.Id, 2, expiresAt: now.AddDays(-8), revokedAt: now.AddDays(-9)),
                // A spent token presented again revokes the whole family (ADR 0002), so it stays one lifetime past expiry.
                Token(user.Id, 3, expiresAt: now.AddDays(1), revokedAt: now.AddMinutes(-1)),
                Token(user.Id, 4, expiresAt: now.AddDays(1), revokedAt: null),
                Token(user.Id, 5, expiresAt: now.AddDays(-1), revokedAt: now.AddDays(-2)));
            await db.SaveChangesAsync(CancellationToken);
        }

        // A new host runs the maintenance job on startup.
        await using var api = Fixture.Api.WithWebHostBuilder(_ => { });
        api.StartServer();

        var kept = await EventuallyAsync(
            async () =>
            {
                await using var scope = Fixture.Api.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<FieldTrackDbContext>();
                var hashes = await db.RefreshTokens.Select(token => token.TokenHash).ToListAsync(CancellationToken);
                return hashes.Select(hash => hash[0]).Order().ToList();
            },
            hashes => hashes.Count == 3);

        kept.ShouldBe([(byte)3, (byte)4, (byte)5]);
    }

    private static RefreshToken Token(Guid userId, byte hash, DateTimeOffset expiresAt, DateTimeOffset? revokedAt) => new()
    {
        UserId = userId,
        TokenHash = [hash],
        CreatedAt = expiresAt.AddDays(-7),
        ExpiresAt = expiresAt,
        RevokedAt = revokedAt,
    };

    private static string PartitionName(DateOnly day) => $"readings_{day.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";

    private async Task<List<string>> ReadingPartitionsAsync()
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT c.relname FROM pg_inherits i JOIN pg_class c ON c.oid = i.inhrelid WHERE i.inhparent = 'readings'::regclass",
            connection);
        await using var reader = await command.ExecuteReaderAsync(CancellationToken);
        var names = new List<string>();
        while (await reader.ReadAsync(CancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(Fixture.DatabaseConnectionString);
        await connection.OpenAsync(CancellationToken);
        return connection;
    }

    private static async Task<T> EventuallyAsync<T>(Func<Task<T>> read, Func<T, bool> done)
    {
        var deadline = DateTime.UtcNow + _patience;
        while (true)
        {
            var value = await read();
            if (done(value) || DateTime.UtcNow > deadline)
            {
                return value;
            }

            await Task.Delay(100, CancellationToken);
        }
    }
}
