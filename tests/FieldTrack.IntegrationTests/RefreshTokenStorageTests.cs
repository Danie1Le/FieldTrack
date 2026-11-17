using FieldTrack.Infrastructure.Identity;
using FieldTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FieldTrack.IntegrationTests;

public sealed class RefreshTokenStorageTests(FieldTrackFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Two_refresh_tokens_cannot_share_a_hash()
    {
        await using var scope = Fixture.Api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FieldTrackDbContext>();
        var user = await AddUserAsync(db);
        byte[] hash = [1, 2, 3];
        db.RefreshTokens.AddRange(NewToken(user, hash), NewToken(user, hash));

        await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync(CancellationToken));
    }

    [Fact]
    public async Task Deleting_a_user_deletes_their_refresh_tokens()
    {
        await using var scope = Fixture.Api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FieldTrackDbContext>();
        var user = await AddUserAsync(db);
        db.RefreshTokens.AddRange(NewToken(user, [1]), NewToken(user, [2]));
        await db.SaveChangesAsync(CancellationToken);

        await db.Users.Where(u => u.Id == user.Id).ExecuteDeleteAsync(CancellationToken);

        (await db.RefreshTokens.CountAsync(token => token.UserId == user.Id, CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task Tables_and_columns_are_snake_case_and_user_keys_are_uuid()
    {
        await using var connection = new NpgsqlConnection(Fixture.DatabaseConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT data_type FROM information_schema.columns WHERE table_name = 'refresh_tokens' AND column_name = 'user_id'",
            connection);

        // Hand-written SQL (ADR 0003, ADR 0004) joins on these names without quoting them.
        (await command.ExecuteScalarAsync(CancellationToken)).ShouldBe("uuid");
    }

    private static async Task<FieldTrackUser> AddUserAsync(FieldTrackDbContext db)
    {
        var user = new FieldTrackUser { Id = Guid.NewGuid(), UserName = "storage@example.com", DisplayName = "Storage" };
        db.Users.Add(user);
        await db.SaveChangesAsync(CancellationToken);
        return user;
    }

    private static RefreshToken NewToken(FieldTrackUser user, byte[] hash) => new()
    {
        UserId = user.Id,
        TokenHash = hash,
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
    };
}
