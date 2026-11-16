using FieldTrack.Api.Auth;
using FieldTrack.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace FieldTrack.IntegrationTests;

/// <summary>
/// Base for tests against the API host: each test starts from an empty database. Tests share one
/// database and reset it before each test, so test collections run serially (xunit.runner.json).
/// </summary>
public abstract class IntegrationTest(FieldTrackFixture fixture) : IAsyncLifetime
{
    /// <summary>The shared containers and API host.</summary>
    protected FieldTrackFixture Fixture { get; } = fixture;

    /// <summary>Cancelled when the test run is aborted or the test times out.</summary>
    protected static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        await Fixture.ResetDatabaseAsync();
        // The reset also deletes the users the host seeded at startup; seed them again.
        await UserSeeder.SeedAsync(Fixture.Api.Services);

        // Unlike a freshly started host, let tests sign in as the Admin with the configured password.
        // AuthStartupTests covers the first-sign-in password change (#61).
        await WithUsersAsync(async users =>
        {
            var admin = (await users.FindByEmailAsync(FieldTrackApiFactory.AdminEmail)).ShouldNotBeNull();
            admin.MustChangePassword = false;
            (await users.UpdateAsync(admin)).Succeeded.ShouldBeTrue();
        });
    }

    /// <summary>Runs <paramref name="action"/> with a <see cref="UserManager{TUser}"/> from a fresh scope of the shared host.</summary>
    protected async Task WithUsersAsync(Func<UserManager<FieldTrackUser>, Task> action)
    {
        await using var scope = Fixture.Api.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<UserManager<FieldTrackUser>>());
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
