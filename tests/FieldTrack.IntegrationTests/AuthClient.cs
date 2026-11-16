using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace FieldTrack.IntegrationTests;

/// <summary>Calls the sign-in endpoints the way the dashboard does.</summary>
internal static class AuthClient
{
    /// <summary>The body of a successful sign-in, refresh or password change.</summary>
    public sealed record Tokens(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken);

    public static Task<HttpResponseMessage> PostSignInAsync(HttpClient client, string email, string password) =>
        client.PostAsJsonAsync("/auth/sign-in", new { email, password }, TestContext.Current.CancellationToken);

    public static Task<HttpResponseMessage> PostRefreshAsync(HttpClient client, string refreshToken) =>
        client.PostAsJsonAsync("/auth/refresh", new { refreshToken }, TestContext.Current.CancellationToken);

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
