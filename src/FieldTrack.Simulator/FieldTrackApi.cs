using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FieldTrack.Domain.Assets;

namespace FieldTrack.Simulator;

/// <summary>An Asset as the API returns it.</summary>
internal sealed record ApiAsset(Guid Id, string Name, AssetType Type);

/// <summary>A Device as the API returns it.</summary>
internal sealed record ApiDevice(Guid Id, string SerialNumber, bool Archived);

/// <summary>An Alert Rule as the API returns it.</summary>
internal sealed record ApiAlertRule(Guid Id, string Name);

/// <summary>A Geofence as the API returns it.</summary>
internal sealed record ApiGeofence(Guid Id, string Name);

/// <summary>An Attachment as the API returns it.</summary>
internal sealed record ApiAttachment(Guid Id, Guid DeviceId, Guid AssetId, DateTimeOffset? DetachedAt);

/// <summary>
/// The REST calls <c>seed</c> needs, made as a signed-in Admin. The simulator is a client of the platform, so it
/// shares no types with the API project and fails the way any other client would when the contract changes.
/// </summary>
internal sealed class FieldTrackApi
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly HttpClient _http;

    private FieldTrackApi(HttpClient http) => _http = http;

    /// <summary>
    /// Signs in and sends the access token on every later request from <paramref name="http"/>.
    /// The first Admin's configured password is temporary (#61) and must be replaced before it signs in.
    /// </summary>
    public static async Task<FieldTrackApi> SignInAsync(HttpClient http, string email, string password, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync("/auth/sign-in", new { email, password }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new SimulatorException(
                $"{email} still has a temporary password. Choose a new one with POST /auth/change-password, then give the simulator the new password, "
                + "or give it the temporary one in FIELDTRACK_ADMIN_TEMPORARY_PASSWORD to have it do that.");
        }

        return await SignedInAsync(http, response, "Sign-in", cancellationToken);
    }

    /// <summary>
    /// Signs in where nobody is at hand to replace the first Admin's temporary password (#61), such as the Azure demo:
    /// an Admin still on <paramref name="temporaryPassword"/> is moved to <paramref name="password"/>, and one already
    /// moved signs in with <paramref name="password"/>.
    /// </summary>
    /// <exception cref="SignInRefusedException">Neither password signs in, or the account is locked out.</exception>
    public static async Task<FieldTrackApi> SignInReplacingTemporaryPasswordAsync(
        HttpClient http, string email, string temporaryPassword, string password, CancellationToken cancellationToken)
    {
        // The temporary password fails once the Admin has moved on; that failure counts towards lockout, but the
        // successful sign-in right after it resets the count.
        using var response = await http.PostAsJsonAsync("/auth/sign-in", new { email, password = temporaryPassword }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return await SignInAsync(http, email, password, cancellationToken);
        }

        if (response.StatusCode != HttpStatusCode.Forbidden)
        {
            return await SignedInAsync(http, response, "Sign-in", cancellationToken);
        }

        using var changed = await http.PostAsJsonAsync("/auth/change-password", new { email, currentPassword = temporaryPassword, newPassword = password }, cancellationToken);
        return await SignedInAsync(http, changed, "Change password", cancellationToken);
    }

    /// <summary>Lists the Assets that are not Archived.</summary>
    public Task<ApiAsset[]> ListAssetsAsync(CancellationToken cancellationToken) => GetAsync<ApiAsset[]>("/assets", cancellationToken);

    /// <summary>Lists every Device, Archived ones too: an Archived serial stays reserved.</summary>
    public Task<ApiDevice[]> ListDevicesAsync(CancellationToken cancellationToken) =>
        GetAsync<ApiDevice[]>("/devices?includeArchived=true", cancellationToken);

    /// <summary>Lists every Attachment, newest first: one call however large the fleet.</summary>
    public Task<ApiAttachment[]> ListAttachmentsAsync(CancellationToken cancellationToken) =>
        GetAsync<ApiAttachment[]>("/attachments", cancellationToken);

    /// <summary>Creates an Asset.</summary>
    public Task<ApiAsset> CreateAssetAsync(string name, AssetType type, CancellationToken cancellationToken) =>
        PostAsync<ApiAsset>("/assets", new { name, type }, cancellationToken);

    /// <summary>Registers a Device.</summary>
    public Task<ApiDevice> CreateDeviceAsync(string serialNumber, CancellationToken cancellationToken) =>
        PostAsync<ApiDevice>("/devices", new { serialNumber }, cancellationToken);

    /// <summary>Attaches a Device to an Asset from now on.</summary>
    public Task<ApiAttachment> AttachAsync(Guid deviceId, Guid assetId, CancellationToken cancellationToken) =>
        PostAsync<ApiAttachment>("/attachments", new { deviceId, assetId }, cancellationToken);

    /// <summary>Lists every Alert Rule.</summary>
    public Task<ApiAlertRule[]> ListAlertRulesAsync(CancellationToken cancellationToken) => GetAsync<ApiAlertRule[]>("/alert-rules", cancellationToken);

    /// <summary>Lists every Geofence.</summary>
    public Task<ApiGeofence[]> ListGeofencesAsync(CancellationToken cancellationToken) => GetAsync<ApiGeofence[]>("/geofences", cancellationToken);

    /// <summary>Creates a Geofence from <paramref name="request"/>, the JSON of <c>POST /geofences</c>.</summary>
    public Task<ApiGeofence> CreateGeofenceAsync(object request, CancellationToken cancellationToken) =>
        PostAsync<ApiGeofence>("/geofences", request, cancellationToken);

    /// <summary>Creates an Alert Rule from <paramref name="request"/>, the JSON of <c>POST /alert-rules</c>.</summary>
    public Task<ApiAlertRule> CreateAlertRuleAsync(object request, CancellationToken cancellationToken) =>
        PostAsync<ApiAlertRule>("/alert-rules", request, cancellationToken);

    /// <summary>
    /// Creates an Operator (the role of a dashboard user) with a random password and returns its access token, so a benchmark can listen
    /// as one. The user stays behind; <paramref name="anonymous"/> must not carry the Admin's token.
    /// </summary>
    public async Task<string> CreateOperatorTokenAsync(HttpClient anonymous, CancellationToken cancellationToken)
    {
        var email = $"bench-{Guid.NewGuid():N}@bench.example";
        var temporary = $"Tmp-{Guid.NewGuid():N}-aA1!";
        var password = $"Pw-{Guid.NewGuid():N}-aA1!";
        await PostAsync<JsonElement>("/users", new { email, displayName = "Bench Operator", role = "Operator", temporaryPassword = temporary }, cancellationToken);
        using var response = await anonymous.PostAsJsonAsync("/auth/change-password", new { email, currentPassword = temporary, newPassword = password }, cancellationToken);
        await EnsureSuccessAsync(response, "Change password");
        return (await response.Content.ReadFromJsonAsync<TokenResponse>(_json, cancellationToken))!.AccessToken;
    }

    // Sends the access token in response on every later request from http.
    private static async Task<FieldTrackApi> SignedInAsync(HttpClient http, HttpResponseMessage response, string what, CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new SignInRefusedException($"{what} failed with 401: wrong email or password, or the account is locked out.");
        }

        await EnsureSuccessAsync(response, what);
        var tokens = await response.Content.ReadFromJsonAsync<TokenResponse>(_json, cancellationToken);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        return new FieldTrackApi(http);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string what)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        // The request is not echoed: sign-in carries a password.
        var body = await response.Content.ReadAsStringAsync();
        throw new SimulatorException($"{what} failed with {(int)response.StatusCode}: {(body.Length > 500 ? body[..500] : body)}");
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(path, cancellationToken);
        await EnsureSuccessAsync(response, $"GET {path}");
        return (await response.Content.ReadFromJsonAsync<T>(_json, cancellationToken))!;
    }

    private async Task<T> PostAsync<T>(string path, object body, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(path, body, _json, cancellationToken);
        await EnsureSuccessAsync(response, $"POST {path}");
        return (await response.Content.ReadFromJsonAsync<T>(_json, cancellationToken))!;
    }

    private sealed record TokenResponse(string AccessToken);
}
