using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using static FieldTrack.IntegrationTests.FieldTrackApiFactory;

namespace FieldTrack.IntegrationTests;

public sealed class UserTests(FieldTrackFixture fixture) : IntegrationTest(fixture)
{
    private const string OperatorEmail = "operator@fieldtrack.test";
    private const string OperatorPassword = "Operator-pass-1";

    [Fact]
    public async Task New_user_must_replace_the_temporary_password_before_getting_tokens()
    {
        using var admin = await AdminClientAsync();
        using var created = await AuthClient.PostUserAsync(admin, OperatorEmail, "Operator", AuthClient.TemporaryPassword);
        using var client = Fixture.Api.CreateClient();

        using var withTemporary = await AuthClient.PostSignInAsync(client, OperatorEmail, AuthClient.TemporaryPassword);
        using var changed = await AuthClient.PostChangePasswordAsync(client, OperatorEmail, AuthClient.TemporaryPassword, OperatorPassword);
        var tokens = await AuthClient.ReadTokensAsync(changed);
        using var withOwn = await AuthClient.PostSignInAsync(client, OperatorEmail, OperatorPassword);
        using var withTemporaryAgain = await AuthClient.PostSignInAsync(client, OperatorEmail, AuthClient.TemporaryPassword);

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        withTemporary.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        AuthClient.RolesOf(tokens.AccessToken).ShouldBe(["Operator"]);
        withOwn.StatusCode.ShouldBe(HttpStatusCode.OK);
        withTemporaryAgain.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(AuthClient.TemporaryPassword)]
    [InlineData("short")]
    public async Task Password_change_to_the_same_or_a_weak_password_is_refused_and_keeps_the_change_required(string newPassword)
    {
        using var admin = await AdminClientAsync();
        using var created = await AuthClient.PostUserAsync(admin, OperatorEmail, "Operator", AuthClient.TemporaryPassword);
        using var client = Fixture.Api.CreateClient();

        using var changed = await AuthClient.PostChangePasswordAsync(client, OperatorEmail, AuthClient.TemporaryPassword, newPassword);
        using var signIn = await AuthClient.PostSignInAsync(client, OperatorEmail, AuthClient.TemporaryPassword);

        changed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        signIn.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Admin_creates_another_Admin()
    {
        using var admin = await AdminClientAsync();
        using var created = await AuthClient.PostUserAsync(admin, OperatorEmail, "Admin", AuthClient.TemporaryPassword);
        using var client = Fixture.Api.CreateClient();

        using var changed = await AuthClient.PostChangePasswordAsync(client, OperatorEmail, AuthClient.TemporaryPassword, OperatorPassword);

        AuthClient.RolesOf((await AuthClient.ReadTokensAsync(changed)).AccessToken).ShouldBe(["Admin"]);
    }

    [Fact]
    public async Task Password_change_with_a_wrong_current_password_is_refused()
    {
        using var admin = await AdminClientAsync();
        using var created = await AuthClient.PostUserAsync(admin, OperatorEmail, "Operator", AuthClient.TemporaryPassword);
        using var client = Fixture.Api.CreateClient();

        using var response = await AuthClient.PostChangePasswordAsync(client, OperatorEmail, "Wrong-pass-1", OperatorPassword);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Password_change_revokes_the_users_other_refresh_tokens()
    {
        using var client = Fixture.Api.CreateClient();
        var before = await AuthClient.SignInAsync(client, AdminEmail, AdminPassword);

        using var changed = await AuthClient.PostChangePasswordAsync(client, AdminEmail, AdminPassword, "New-admin-pass-1");
        using var refreshed = await AuthClient.PostRefreshAsync(client, before.RefreshToken);

        changed.StatusCode.ShouldBe(HttpStatusCode.OK);
        refreshed.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("Viewer", AuthClient.TemporaryPassword)]
    [InlineData("Operator", "short")]
    public async Task Create_user_refuses_an_unknown_role_or_a_weak_password(string role, string temporaryPassword)
    {
        using var admin = await AdminClientAsync();

        using var response = await AuthClient.PostUserAsync(admin, OperatorEmail, role, temporaryPassword);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_user_refuses_an_email_already_in_use()
    {
        using var admin = await AdminClientAsync();

        using var response = await AuthClient.PostUserAsync(admin, AdminEmail, "Operator", AuthClient.TemporaryPassword);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Admin_lists_users_with_their_roles_and_pending_password_changes()
    {
        using var admin = await AdminClientAsync();
        await AuthClient.CreateUserAsync(admin, OperatorEmail, "Operator", OperatorPassword);
        using var created = await AuthClient.PostUserAsync(admin, "new@fieldtrack.test", "Operator", AuthClient.TemporaryPassword);

        var users = await admin.GetFromJsonAsync<JsonElement[]>("/users", CancellationToken);

        // The seeded Admin's false comes from IntegrationTest, which completes their first-sign-in change.
        users.ShouldNotBeNull()
            .Select(user => (user.GetProperty("email").GetString(), user.GetProperty("role").GetString(), user.GetProperty("mustChangePassword").GetBoolean()))
            .ShouldBe([(AdminEmail, "Admin", false), (OperatorEmail, "Operator", false), ("new@fieldtrack.test", "Operator", true)], ignoreOrder: true);
    }

    [Fact]
    public async Task Revoking_a_users_refresh_tokens_stops_them_refreshing()
    {
        using var admin = await AdminClientAsync();
        await AuthClient.CreateUserAsync(admin, OperatorEmail, "Operator", OperatorPassword);
        using var client = Fixture.Api.CreateClient();
        var operatorTokens = await AuthClient.SignInAsync(client, OperatorEmail, OperatorPassword);
        var operatorId = new JsonWebToken(operatorTokens.AccessToken).Subject;

        using var revoked = await admin.DeleteAsync($"/users/{operatorId}/refresh-tokens", CancellationToken);
        using var refreshed = await AuthClient.PostRefreshAsync(client, operatorTokens.RefreshToken);

        revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        refreshed.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Revoking_an_unknown_users_refresh_tokens_is_not_found()
    {
        using var admin = await AdminClientAsync();

        using var response = await admin.DeleteAsync($"/users/{Guid.NewGuid()}/refresh-tokens", CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Operators_cannot_manage_users()
    {
        using var admin = await AdminClientAsync();
        await AuthClient.CreateUserAsync(admin, OperatorEmail, "Operator", OperatorPassword);
        using var client = Fixture.Api.CreateClient();
        await AuthClient.AuthorizeAsync(client, OperatorEmail, OperatorPassword);

        using var list = await client.GetAsync("/users", CancellationToken);
        using var create = await AuthClient.PostUserAsync(client, "another@fieldtrack.test", "Admin", AuthClient.TemporaryPassword);
        using var revoke = await client.DeleteAsync($"/users/{Guid.NewGuid()}/refresh-tokens", CancellationToken);

        list.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        create.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        revoke.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Nobody_registers_themselves()
    {
        using var client = Fixture.Api.CreateClient();

        using var response = await AuthClient.PostUserAsync(client, OperatorEmail, "Operator", AuthClient.TemporaryPassword);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private async Task<HttpClient> AdminClientAsync()
    {
        var client = Fixture.Api.CreateClient();
        await AuthClient.AuthorizeAsync(client, AdminEmail, AdminPassword);
        return client;
    }
}
