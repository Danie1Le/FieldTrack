using System.Net;
using static FieldTrack.IntegrationTests.FieldTrackApiFactory;

namespace FieldTrack.IntegrationTests;

public sealed class RefreshTokenTests(FieldTrackFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Refresh_returns_new_tokens_and_spends_the_old_refresh_token()
    {
        using var client = Fixture.Api.CreateClient();
        var signedIn = await AuthClient.SignInAsync(client, AdminEmail, AdminPassword);

        using var refreshed = await AuthClient.PostRefreshAsync(client, signedIn.RefreshToken);
        var tokens = await AuthClient.ReadTokensAsync(refreshed);
        using var replayed = await AuthClient.PostRefreshAsync(client, signedIn.RefreshToken);

        tokens.RefreshToken.ShouldNotBe(signedIn.RefreshToken);
        tokens.AccessToken.ShouldNotBeNullOrWhiteSpace();
        replayed.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Rotated_refresh_token_keeps_refreshing()
    {
        using var client = Fixture.Api.CreateClient();
        var signedIn = await AuthClient.SignInAsync(client, AdminEmail, AdminPassword);

        using var first = await AuthClient.PostRefreshAsync(client, signedIn.RefreshToken);
        using var second = await AuthClient.PostRefreshAsync(client, (await AuthClient.ReadTokensAsync(first)).RefreshToken);

        second.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Replaying_a_spent_refresh_token_ends_the_session_it_was_rotated_into()
    {
        using var client = Fixture.Api.CreateClient();
        var signedIn = await AuthClient.SignInAsync(client, AdminEmail, AdminPassword);
        using var refreshed = await AuthClient.PostRefreshAsync(client, signedIn.RefreshToken);
        var current = await AuthClient.ReadTokensAsync(refreshed);

        using var replayed = await AuthClient.PostRefreshAsync(client, signedIn.RefreshToken);
        using var afterReplay = await AuthClient.PostRefreshAsync(client, current.RefreshToken);

        afterReplay.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("not a token")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task Unknown_refresh_token_is_refused(string refreshToken)
    {
        using var client = Fixture.Api.CreateClient();

        using var response = await AuthClient.PostRefreshAsync(client, refreshToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
