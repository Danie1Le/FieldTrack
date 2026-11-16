using FieldTrack.Api.Auth;

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
        // The reset also deletes the users the host seeded at startup; seed them again so every
        // test starts where a freshly started host does.
        await UserSeeder.SeedAsync(Fixture.Api.Services, CancellationToken);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
