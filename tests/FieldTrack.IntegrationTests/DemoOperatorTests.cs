using System.Net;
using FieldTrack.Api.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.JsonWebTokens;

namespace FieldTrack.IntegrationTests;

public sealed class DemoOperatorTests(FieldTrackFixture fixture) : IntegrationTest(fixture)
{
    private const string DemoEmail = "demo@fieldtrack.test";
    private const string DemoPassword = "Demo-pass-1";

    [Fact]
    public async Task Demo_mode_creates_the_Demo_Operator_with_the_configured_password()
    {
        await using var api = DemoApi(DemoPassword);
        using var client = api.CreateClient();

        var tokens = await AuthClient.SignInAsync(client, DemoEmail, DemoPassword);

        new JsonWebToken(tokens.AccessToken).Claims
            .Where(claim => claim.Type == "role").Select(claim => claim.Value).ShouldBe(["Operator"]);
    }

    [Fact]
    public async Task Startup_resets_the_Demo_Operator_to_the_configured_password()
    {
        await using (var previousDeploy = DemoApi("Previous-demo-pass-1"))
        {
            using var started = previousDeploy.CreateClient();
        }

        await using var api = DemoApi(DemoPassword);
        using var client = api.CreateClient();

        using var previous = await AuthClient.PostSignInAsync(client, DemoEmail, "Previous-demo-pass-1");
        using var current = await AuthClient.PostSignInAsync(client, DemoEmail, DemoPassword);

        previous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        current.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Demo_Operator_cannot_change_its_password()
    {
        await using var api = DemoApi(DemoPassword);
        using var client = api.CreateClient();

        using var response = await AuthClient.PostChangePasswordAsync(client, DemoEmail, DemoPassword, "Hijacked-pass-1");
        using var signIn = await AuthClient.PostSignInAsync(client, DemoEmail, DemoPassword);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        signIn.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Wrong_passwords_do_not_lock_the_Demo_Operator_out()
    {
        // Its password is shown on the login page, so a lockout would only let visitors lock each other out.
        await using var api = DemoApi(DemoPassword);
        using var client = api.CreateClient();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var failed = await AuthClient.PostSignInAsync(client, DemoEmail, "Wrong-pass-1");
        }

        using var response = await AuthClient.PostSignInAsync(client, DemoEmail, DemoPassword);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private WebApplicationFactory<Program> DemoApi(string password) =>
        Fixture.Api.WithWebHostBuilder(builder => builder
            .UseSetting(UserSeeder.DemoEmailKey, DemoEmail)
            .UseSetting(UserSeeder.DemoPasswordKey, password));
}
