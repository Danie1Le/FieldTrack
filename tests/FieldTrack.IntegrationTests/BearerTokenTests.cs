using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using static FieldTrack.IntegrationTests.FieldTrackApiFactory;

namespace FieldTrack.IntegrationTests;

// Nothing is ever mapped at these paths, so a signed-in request gets 404 and a signed-out one 401:
// the fallback policy requires a signed-in user even where nothing is mapped.
public sealed class BearerTokenTests(FieldTrackFixture fixture) : IntegrationTest(fixture)
{
    private const string UnmappedPath = "/unmapped";
    private const string UnmappedHubPath = "/hubs/unmapped";

    [Fact]
    public async Task Requests_without_a_token_are_refused()
    {
        using var client = Fixture.Api.CreateClient();

        using var response = await client.GetAsync(UnmappedPath, CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Access_token_in_the_Authorization_header_signs_the_request_in()
    {
        using var client = Fixture.Api.CreateClient();
        await AuthClient.AuthorizeAsync(client, AdminEmail, AdminPassword);

        using var response = await client.GetAsync(UnmappedPath, CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(-5, HttpStatusCode.NotFound)]
    [InlineData(1, HttpStatusCode.Unauthorized)]
    public async Task Access_token_expires_after_15_minutes_with_no_clock_skew(int secondsPastExpiry, HttpStatusCode expected)
    {
        var time = new AdjustableTimeProvider { Offset = -(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(secondsPastExpiry)) };
        await using var api = Fixture.Api.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(time)));
        using var client = api.CreateClient();
        await AuthClient.AuthorizeAsync(client, AdminEmail, AdminPassword);

        using var response = await client.GetAsync(UnmappedPath, CancellationToken);

        response.StatusCode.ShouldBe(expected);
    }

    [Fact]
    public async Task Access_token_signed_with_another_key_is_refused()
    {
        using var client = Fixture.Api.CreateClient();
        await using var otherApi = Fixture.Api.WithWebHostBuilder(_ => { });
        // A derived host gets its own random signing key from the factory.
        using var otherClient = otherApi.CreateClient();
        var foreign = await AuthClient.SignInAsync(otherClient, AdminEmail, AdminPassword);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", foreign.AccessToken);

        using var response = await client.GetAsync(UnmappedPath, CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SignalR_hubs_accept_the_access_token_from_the_query_string()
    {
        using var client = Fixture.Api.CreateClient();
        var tokens = await AuthClient.SignInAsync(client, AdminEmail, AdminPassword);

        using var signedOut = await client.GetAsync(UnmappedHubPath, CancellationToken);
        using var signedIn = await client.GetAsync($"{UnmappedHubPath}?access_token={tokens.AccessToken}", CancellationToken);

        signedOut.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        signedIn.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Other_paths_ignore_an_access_token_in_the_query_string()
    {
        using var client = Fixture.Api.CreateClient();
        var tokens = await AuthClient.SignInAsync(client, AdminEmail, AdminPassword);

        using var response = await client.GetAsync($"{UnmappedPath}?access_token={tokens.AccessToken}", CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
