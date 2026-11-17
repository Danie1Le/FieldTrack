using System.Net.Http.Headers;
using System.Net.Http.Json;
using FieldTrack.Api.Auth;
using Microsoft.IdentityModel.JsonWebTokens;

namespace FieldTrack.IntegrationTests;

/// <summary>Calls the sign-in endpoints the way the dashboard does.</summary>
internal static class AuthClient
{
    /// <summary>The temporary password <see cref="CreateUserAsync"/> gives new users.</summary>
    public const string TemporaryPassword = "Temporary-pass-1";

    /// <summary>The body of a successful sign-in, refresh or password change.</summary>
    public sealed record Tokens(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken);

    /// <summary>The roles an access token grants.</summary>
    public static IEnumerable<string> RolesOf(string accessToken) =>
        new JsonWebToken(accessToken).Claims.Where(claim => claim.Type == TokenService.RoleClaim).Select(claim => claim.Value);

    public static Task<HttpResponseMessage> PostSignInAsync(HttpClient client, string email, string password) =>
        client.PostAsJsonAsync("/auth/sign-in", new { email, password }, TestContext.Current.CancellationToken);

    public static Task<HttpResponseMessage> PostRefreshAsync(HttpClient client, string refreshToken) =>
        client.PostAsJsonAsync("/auth/refresh", new { refreshToken }, TestContext.Current.CancellationToken);

    public static Task<HttpResponseMessage> PostChangePasswordAsync(HttpClient client, string email, string currentPassword, string newPassword) =>
        client.PostAsJsonAsync("/auth/change-password", new { email, currentPassword, newPassword }, TestContext.Current.CancellationToken);

    public static Task<HttpResponseMessage> PostUserAsync(
        HttpClient adminClient, string email, string role, string temporaryPassword, string displayName = "Test User") =>
        adminClient.PostAsJsonAsync("/users", new { email, displayName, role, temporaryPassword }, TestContext.Current.CancellationToken);

    /// <summary>
    /// Has the Admin behind <paramref name="adminClient"/> create a user, then replaces the temporary
    /// password with <paramref name="password"/> as the user's first sign-in requires.
    /// </summary>
    public static async Task CreateUserAsync(HttpClient adminClient, string email, string role, string password, string displayName = "Test User")
    {
        using var created = await PostUserAsync(adminClient, email, role, TemporaryPassword, displayName);
        created.EnsureSuccessStatusCode();
        using var changed = await PostChangePasswordAsync(adminClient, email, TemporaryPassword, password);
        changed.EnsureSuccessStatusCode();
    }

    public static async Task<Tokens> SignInAsync(HttpClient client, string email, string password)
    {
        using var response = await PostSignInAsync(client, email, password);
        return await ReadTokensAsync(response);
    }

    public static async Task<Tokens> ReadTokensAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Tokens>(TestContext.Current.CancellationToken))!;
    }

    /// <summary>Signs in and sends the access token on every later request from <paramref name="client"/>.</summary>
    public static async Task<Tokens> AuthorizeAsync(HttpClient client, string email, string password)
    {
        var tokens = await SignInAsync(client, email, password);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return tokens;
    }
}
