using System.Net;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task Replaying_a_spent_refresh_token_revokes_the_token_it_was_rotated_into()
    {
        using var client = Fixture.Api.CreateClient();
        var signedIn = await AuthClient.SignInAsync(client, AdminEmail, AdminPassword);
        using var refreshed = await AuthClient.PostRefreshAsync(client, signedIn.RefreshToken);
        var current = await AuthClient.ReadTokensAsync(refreshed);

        using var replayed = await AuthClient.PostRefreshAsync(client, signedIn.RefreshToken);
        using var afterReplay = await AuthClient.PostRefreshAsync(client, current.RefreshToken);

        afterReplay.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Replaying_a_spent_refresh_token_revokes_every_refresh_token_of_that_user()
    {
        using var client = Fixture.Api.CreateClient();
        var signedIn = await AuthClient.SignInAsync(client, AdminEmail, AdminPassword);
        var otherSession = await AuthClient.SignInAsync(client, AdminEmail, AdminPassword);
        using var refreshed = await AuthClient.PostRefreshAsync(client, signedIn.RefreshToken);

        using var replayed = await AuthClient.PostRefreshAsync(client, signedIn.RefreshToken);
        using var otherAfterReplay = await AuthClient.PostRefreshAsync(client, otherSession.RefreshToken);

        otherAfterReplay.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Failed_sign_ins_that_lock_a_user_out_do_not_end_their_session()
    {
        // Anyone who knows the email can trigger this lockout, so it blocks new sign-ins only.
        using var client = Fixture.Api.CreateClient();
        var signedIn = await AuthClient.SignInAsync(client, AdminEmail, AdminPassword);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var failed = await AuthClient.PostSignInAsync(client, AdminEmail, "Wrong-pass-1");
        }

        using var signIn = await AuthClient.PostSignInAsync(client, AdminEmail, AdminPassword);
        using var refreshed = await AuthClient.PostRefreshAsync(client, signedIn.RefreshToken);

        signIn.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        refreshed.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Two_refreshes_racing_with_one_token_leave_no_usable_refresh_token()
    {
        using var client = Fixture.Api.CreateClient();
        // The race is narrow, so run it several times.
        for (var round = 0; round < 10; round++)
        {
            var signedIn = await AuthClient.SignInAsync(client, AdminEmail, AdminPassword);

            var responses = await Task.WhenAll(
                AuthClient.PostRefreshAsync(client, signedIn.RefreshToken),
                AuthClient.PostRefreshAsync(client, signedIn.RefreshToken));
            using var winner = responses.Single(response => response.StatusCode == HttpStatusCode.OK);
            using var loser = responses.Single(response => response.StatusCode == HttpStatusCode.Unauthorized);
            using var afterRace = await AuthClient.PostRefreshAsync(client, (await AuthClient.ReadTokensAsync(winner)).RefreshToken);

            // ADR 0002: a token presented twice counts as copied, whichever request wins.
            afterRace.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
    }

    [Theory]
    [InlineData(-1, HttpStatusCode.OK)]
    [InlineData(1, HttpStatusCode.Unauthorized)]
    public async Task Refresh_token_expires_after_7_days(int minutesPastExpiry, HttpStatusCode expected)
    {
        var time = new AdjustableTimeProvider();
        await using var api = Fixture.Api.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(time)));
        using var client = api.CreateClient();
        time.Offset = -(TimeSpan.FromDays(7) + TimeSpan.FromMinutes(minutesPastExpiry));
        var signedIn = await AuthClient.SignInAsync(client, AdminEmail, AdminPassword);
        time.Offset = TimeSpan.Zero;

        using var response = await AuthClient.PostRefreshAsync(client, signedIn.RefreshToken);

        response.StatusCode.ShouldBe(expected);
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
