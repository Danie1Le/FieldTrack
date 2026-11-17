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
                $"{email} still has a temporary password. Choose a new one with POST /auth/change-password, then give the simulator the new password.");
        }

        await EnsureSuccessAsync(response, "Sign-in");
        var tokens = await response.Content.ReadFromJsonAsync<TokenResponse>(_json, cancellationToken);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        return new FieldTrackApi(http);
    }

    /// <summary>Lists the Assets that are not Archived.</summary>
    public Task<ApiAsset[]> ListAssetsAsync(CancellationToken cancellationToken) => GetAsync<ApiAsset[]>("/assets", cancellationToken);

    /// <summary>Lists every Device, Archived ones too: an Archived serial stays reserved.</summary>
    public Task<ApiDevice[]> ListDevicesAsync(CancellationToken cancellationToken) =>
        GetAsync<ApiDevice[]>("/devices?includeArchived=true", cancellationToken);

    /// <summary>Lists one Device's Attachments, newest first.</summary>
    public Task<ApiAttachment[]> ListAttachmentsAsync(Guid deviceId, CancellationToken cancellationToken) =>
        GetAsync<ApiAttachment[]>($"/attachments?deviceId={deviceId}", cancellationToken);

    /// <summary>Creates an Asset.</summary>
    public Task<ApiAsset> CreateAssetAsync(string name, AssetType type, CancellationToken cancellationToken) =>
        PostAsync<ApiAsset>("/assets", new { name, type }, cancellationToken);

    /// <summary>Registers a Device.</summary>
    public Task<ApiDevice> CreateDeviceAsync(string serialNumber, CancellationToken cancellationToken) =>
        PostAsync<ApiDevice>("/devices", new { serialNumber }, cancellationToken);

    /// <summary>Attaches a Device to an Asset from now on.</summary>
    public Task<ApiAttachment> AttachAsync(Guid deviceId, Guid assetId, CancellationToken cancellationToken) =>
        PostAsync<ApiAttachment>("/attachments", new { deviceId, assetId }, cancellationToken);

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
