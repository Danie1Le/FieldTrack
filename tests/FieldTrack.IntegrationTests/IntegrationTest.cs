namespace FieldTrack.IntegrationTests;

/// <summary>
/// Base for tests against the API host: each test starts from an empty database.
/// </summary>
public abstract class IntegrationTest(FieldTrackFixture fixture) : IAsyncLifetime
{
    /// <summary>The shared containers and API host.</summary>
    protected FieldTrackFixture Fixture { get; } = fixture;

    /// <summary>Cancelled when the test run is aborted or the test times out.</summary>
    protected static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    /// <inheritdoc />
    public virtual async ValueTask InitializeAsync() => await Fixture.ResetDatabaseAsync();

    /// <inheritdoc />
    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
