namespace FieldTrack.IntegrationTests;

/// <summary>
/// Base for tests against the API host and its containers.
/// </summary>
public abstract class IntegrationTest(FieldTrackFixture fixture)
{
    /// <summary>The shared containers and API host.</summary>
    protected FieldTrackFixture Fixture { get; } = fixture;

    /// <summary>Cancelled when the test run is aborted or the test times out.</summary>
    protected static CancellationToken CancellationToken => TestContext.Current.CancellationToken;
}
