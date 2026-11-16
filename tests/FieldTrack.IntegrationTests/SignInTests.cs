using System.Net;
using FieldTrack.Api.Auth;
using Microsoft.IdentityModel.JsonWebTokens;
using static FieldTrack.IntegrationTests.FieldTrackApiFactory;

namespace FieldTrack.IntegrationTests;

public sealed class SignInTests(FieldTrackFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Admin_signs_in_with_email_and_password_and_gets_a_15_minute_access_token_and_a_refresh_token()
    {
        using var client = Fixture.Api.CreateClient();

        var tokens = await AuthClient.SignInAsync(client, AdminEmail, AdminPassword);

        var accessToken = new JsonWebToken(tokens.AccessToken);
        accessToken.Alg.ShouldBe("HS256");
        (accessToken.ValidTo - accessToken.IssuedAt).ShouldBe(TimeSpan.FromMinutes(15));
        accessToken.Claims.Where(claim => claim.Type == "role").Select(claim => claim.Value).ShouldBe(["Admin"]);
        accessToken.GetClaim("email").Value.ShouldBe(AdminEmail);
        tokens.AccessTokenExpiresAt.ShouldBe(new DateTimeOffset(accessToken.ValidTo, TimeSpan.Zero));
        tokens.RefreshToken.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Wrong_password_is_refused()
    {
        using var client = Fixture.Api.CreateClient();

        using var response = await AuthClient.PostSignInAsync(client, AdminEmail, "Wrong-pass-1");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_out()
    {
        using var client = Fixture.Api.CreateClient();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var failed = await AuthClient.PostSignInAsync(client, AdminEmail, "Wrong-pass-1");
        }

        using var response = await AuthClient.PostSignInAsync(client, AdminEmail, AdminPassword);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Four_wrong_passwords_do_not_lock_the_account_out()
    {
        using var client = Fixture.Api.CreateClient();
        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var failed = await AuthClient.PostSignInAsync(client, AdminEmail, "Wrong-pass-1");
        }

        using var response = await AuthClient.PostSignInAsync(client, AdminEmail, AdminPassword);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Sign_in_attempts_beyond_the_rate_limit_are_refused_with_429()
    {
        await using var api = Fixture.Api.WithWebHostBuilder(builder =>
            builder.UseSetting($"{AuthOptions.SectionName}:{nameof(AuthOptions.SignInPermitsPerMinute)}", "2"));
        using var client = api.CreateClient();

        using var first = await AuthClient.PostSignInAsync(client, AdminEmail, AdminPassword);
        using var second = await AuthClient.PostSignInAsync(client, AdminEmail, AdminPassword);
        using var third = await AuthClient.PostSignInAsync(client, AdminEmail, AdminPassword);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        third.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Unknown_email_is_refused_like_a_wrong_password()
    {
        using var client = Fixture.Api.CreateClient();

        using var response = await AuthClient.PostSignInAsync(client, "nobody@fieldtrack.test", AdminPassword);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
