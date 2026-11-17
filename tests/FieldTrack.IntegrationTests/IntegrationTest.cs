using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FieldTrack.Api.Auth;
using FieldTrack.Api.Ingest;
using FieldTrack.Infrastructure.Identity;
using FieldTrack.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

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

    /// <summary>
    /// A host where every Asset and Device counts as having Readings, so deletes take the Archive branch
    /// without publishing telemetry first. AssetTests and IngestTests cover the same branch with real Readings.
    /// </summary>
    protected WebApplicationFactory<Program> ApiWhereEverythingHasReadings() =>
        Fixture.Api.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddScoped<IReadingHistory, EverythingHasReadings>()));

    /// <summary>
    /// Starts a host running the ingest worker on a broker session of its own, created before this returns,
    /// so every message published afterwards reaches it. <paramref name="time"/> replaces the clock; pass
    /// <paramref name="clientId"/> to resume a session the test set up; <paramref name="services"/> replaces more.
    /// </summary>
    protected async Task<WebApplicationFactory<Program>> StartIngestAsync(
        TimeProvider? time = null, string? clientId = null, Action<IServiceCollection>? services = null)
    {
        clientId ??= $"fieldtrack-ingest-test-{Guid.NewGuid():N}";
        await TelemetryPublisher.CreateIngestSessionAsync(Fixture, clientId);
        var api = Fixture.Api.WithWebHostBuilder(builder => builder
            .UseSetting($"{IngestOptions.SectionName}:{nameof(IngestOptions.ClientId)}", clientId)
            .ConfigureTestServices(collection =>
            {
                collection.AddHostedService<IngestWorker>();
                if (time is not null)
                {
                    collection.AddSingleton(time);
                }

                services?.Invoke(collection);
            }));
        api.StartServer();
        return api;
    }

    /// <summary>Reads until <paramref name="done"/> holds or 15 s pass, and returns the last value read.</summary>
    protected static async Task<T> EventuallyAsync<T>(Func<Task<T>> read, Func<T, bool> done)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            var value = await read();
            if (done(value) || DateTime.UtcNow > deadline)
            {
                return value;
            }

            await Task.Delay(50, CancellationToken);
        }
    }

    /// <summary>A connection of the test's own to the API's database, for SQL the API does not expose.</summary>
    protected async Task<NpgsqlConnection> OpenDatabaseAsync()
    {
        var connection = new NpgsqlConnection(Fixture.DatabaseConnectionString);
        await connection.OpenAsync(CancellationToken);
        return connection;
    }

    /// <summary>Runs <paramref name="sql"/> with parameters <c>@p0</c>, <c>@p1</c>... and returns the first column of the first row.</summary>
    protected static async Task<object?> SqlAsync(NpgsqlConnection connection, string sql, params object[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        for (var i = 0; i < parameters.Length; i++)
        {
            command.Parameters.AddWithValue($"p{i}", parameters[i]);
        }

        return await command.ExecuteScalarAsync(CancellationToken);
    }

    /// <summary>A client signed in as the Admin, on <paramref name="api"/> or the shared host.</summary>
    protected async Task<HttpClient> AdminClientAsync(WebApplicationFactory<Program>? api = null)
    {
        var client = (api ?? Fixture.Api).CreateClient();
        await AuthClient.AuthorizeAsync(client, FieldTrackApiFactory.AdminEmail, FieldTrackApiFactory.AdminPassword);
        return client;
    }

    /// <summary>POSTs <paramref name="body"/> to <paramref name="path"/>, expects 201 and returns the new id.</summary>
    protected static async Task<string> CreateAsync(HttpClient client, string path, object body)
    {
        using var response = await client.PostAsJsonAsync(path, body, CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken)).GetProperty("id").GetString()!;
    }

    /// <summary>GETs a list endpoint.</summary>
    protected static async Task<JsonElement[]> ListAsync(HttpClient client, string path) =>
        (await client.GetFromJsonAsync<JsonElement[]>(path, CancellationToken)).ShouldNotBeNull();

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
