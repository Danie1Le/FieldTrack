using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FieldTrack.Api.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using static FieldTrack.IntegrationTests.FieldTrackApiFactory;

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

        AuthClient.RolesOf(tokens.AccessToken).ShouldBe(["Operator"]);
        await WithUsersAsync(async users => (await users.FindByEmailAsync(DemoEmail)).ShouldNotBeNull().DisplayName.ShouldBe(UserSeeder.DemoOperatorDisplayName));
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
    public async Task Signed_out_visitors_read_the_Demo_Operator_credentials_in_demo_mode()
    {
        await using var api = DemoApi(DemoPassword);
        using var client = api.CreateClient();

        var credentials = await client.GetFromJsonAsync<JsonElement>("/auth/demo-operator", TestContext.Current.CancellationToken);

        credentials.GetProperty("email").GetString().ShouldBe(DemoEmail);
        credentials.GetProperty("password").GetString().ShouldBe(DemoPassword);
    }

    [Fact]
    public async Task Without_demo_mode_there_are_no_Demo_Operator_credentials()
    {
        using var client = Fixture.Api.CreateClient();

        using var response = await client.GetAsync("/auth/demo-operator", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
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

    [Fact]
    public async Task Startup_refuses_demo_mode_that_names_an_Operator_and_leaves_the_Operator_alone()
    {
        using var admin = Fixture.Api.CreateClient();
        await AuthClient.AuthorizeAsync(admin, AdminEmail, AdminPassword);
        await AuthClient.CreateUserAsync(admin, DemoEmail, "Operator", "Operator-pass-1");

        await using var api = DemoApi(DemoPassword);

        Should.Throw<InvalidOperationException>(() => api.CreateClient()).Message.ShouldContain(DemoOperatorOptions.EmailKey);
        using var client = Fixture.Api.CreateClient();
        using var operatorSignIn = await AuthClient.PostSignInAsync(client, DemoEmail, "Operator-pass-1");
        using var demoSignIn = await AuthClient.PostSignInAsync(client, DemoEmail, DemoPassword);
        operatorSignIn.StatusCode.ShouldBe(HttpStatusCode.OK);
        demoSignIn.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Startup_without_demo_mode_disables_the_Demo_Operator()
    {
        string refreshToken;
        await using (var demoDeploy = DemoApi(DemoPassword))
        {
            using var demoClient = demoDeploy.CreateClient();
            refreshToken = (await AuthClient.SignInAsync(demoClient, DemoEmail, DemoPassword)).RefreshToken;
        }

        await UserSeeder.SeedAsync(Fixture.Api.Services);

        using var client = Fixture.Api.CreateClient();
        using var signIn = await AuthClient.PostSignInAsync(client, DemoEmail, DemoPassword);
        using var refresh = await AuthClient.PostRefreshAsync(client, refreshToken);
        signIn.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        refresh.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Startup_with_another_demo_email_disables_the_previous_Demo_Operator()
    {
        await using (var previousDeploy = DemoApi(DemoPassword))
        {
            using var started = previousDeploy.CreateClient();
        }

        await using var api = DemoApi(DemoPassword, "new-demo@fieldtrack.test");
        using var client = api.CreateClient();

        using var previous = await AuthClient.PostSignInAsync(client, DemoEmail, DemoPassword);
        using var current = await AuthClient.PostSignInAsync(client, "new-demo@fieldtrack.test", DemoPassword);
        previous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        current.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Demo_mode_on_again_brings_the_disabled_Demo_Operator_back()
    {
        await using (var previousDeploy = DemoApi(DemoPassword))
        {
            using var started = previousDeploy.CreateClient();
        }

        await UserSeeder.SeedAsync(Fixture.Api.Services);
        await using var api = DemoApi(DemoPassword);
        using var client = api.CreateClient();

        using var response = await AuthClient.PostSignInAsync(client, DemoEmail, DemoPassword);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Replaying_a_Demo_Operator_refresh_token_signs_out_only_that_visitor()
    {
        // ADR 0002: every visitor shares the Demo Operator, so one replay must not sign the others out.
        await using var api = DemoApi(DemoPassword);
        using var client = api.CreateClient();
        var visitor = await AuthClient.SignInAsync(client, DemoEmail, DemoPassword);
        var otherVisitor = await AuthClient.SignInAsync(client, DemoEmail, DemoPassword);
        using var refreshed = await AuthClient.PostRefreshAsync(client, visitor.RefreshToken);
        var rotated = await AuthClient.ReadTokensAsync(refreshed);

        using var replayed = await AuthClient.PostRefreshAsync(client, visitor.RefreshToken);
        using var visitorAfterReplay = await AuthClient.PostRefreshAsync(client, rotated.RefreshToken);
        using var otherAfterReplay = await AuthClient.PostRefreshAsync(client, otherVisitor.RefreshToken);

        replayed.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        visitorAfterReplay.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        otherAfterReplay.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Replaying_a_Demo_Operator_refresh_token_leaves_another_users_tokens_of_the_same_family_alone()
    {
        await using var api = DemoApi(DemoPassword);
        using var client = api.CreateClient();
        var visitor = await AuthClient.SignInAsync(client, DemoEmail, DemoPassword);
        var admin = await AuthClient.SignInAsync(client, AdminEmail, AdminPassword);
        // Families are only unique per user: give both users' tokens one family.
        await using (var database = await OpenDatabaseAsync())
        {
            await SqlAsync(database, "UPDATE refresh_tokens SET family_id = @p0", Guid.Empty);
        }

        using var refreshed = await AuthClient.PostRefreshAsync(client, visitor.RefreshToken);
        using var replayed = await AuthClient.PostRefreshAsync(client, visitor.RefreshToken);
        using var adminAfterReplay = await AuthClient.PostRefreshAsync(client, admin.RefreshToken);

        replayed.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        adminAfterReplay.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Disabled_Demo_Operator_cannot_refresh_a_token_the_startup_revoke_missed()
    {
        // A refresh committing while startup disables the Demo Operator leaves a token the revoke never saw.
        await using var api = DemoApi(DemoPassword);
        using var client = api.CreateClient();
        var visitor = await AuthClient.SignInAsync(client, DemoEmail, DemoPassword);
        await WithUsersAsync(async users =>
        {
            var demo = (await users.FindByEmailAsync(DemoEmail)).ShouldNotBeNull();
            (await users.SetLockoutEnabledAsync(demo, true)).Succeeded.ShouldBeTrue();
            (await users.SetLockoutEndDateAsync(demo, DateTimeOffset.MaxValue)).Succeeded.ShouldBeTrue();
        });

        using var refresh = await AuthClient.PostRefreshAsync(client, visitor.RefreshToken);

        refresh.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Startup_fails_and_keeps_the_previous_password_when_the_demo_password_is_too_weak()
    {
        await using (var previousDeploy = DemoApi("Previous-demo-pass-1"))
        {
            using var started = previousDeploy.CreateClient();
        }

        await using var weak = DemoApi("short");
        Should.Throw<InvalidOperationException>(() => weak.CreateClient());
        using var client = Fixture.Api.CreateClient();
        using var response = await AuthClient.PostSignInAsync(client, DemoEmail, "Previous-demo-pass-1");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Startup_refuses_demo_mode_that_names_an_Admin_and_leaves_the_Admin_alone()
    {
        await using var api = DemoApi(DemoPassword, AdminEmail);

        Should.Throw<InvalidOperationException>(() => api.CreateClient());
        using var client = Fixture.Api.CreateClient();
        using var response = await AuthClient.PostSignInAsync(client, AdminEmail, AdminPassword);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private WebApplicationFactory<Program> DemoApi(string password, string email = DemoEmail) =>
        Fixture.Api.WithWebHostBuilder(builder => builder
            .UseSetting(DemoOperatorOptions.EmailKey, email)
            .UseSetting(DemoOperatorOptions.PasswordKey, password));
}
