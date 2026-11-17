using FieldTrack.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace FieldTrack.Api.Live;

/// <summary>
/// The SignalR hub dashboards listen on for live changes; <see cref="LivePush"/> sends them after each change commits.
/// Clients only listen, so it has no methods. Pushes carry no delivery guarantee: a dashboard that missed one, or
/// reconnects, reloads <c>GET /assets/live</c> and <c>GET /alerts</c> (ADR 0005).
/// </summary>
[Authorize(Policy = Policies.Read)]
public sealed class LiveHub : Hub
{
    /// <summary>Where the hub is mapped; under the hubs path, so the access token may come in the <c>access_token</c> query parameter.</summary>
    public const string Path = AuthServiceCollectionExtensions.HubsPath + "/live";

    /// <summary>Carries the <see cref="LiveAssetResponse"/> of every Asset a commit changed, one message per commit.</summary>
    public const string AssetsUpdated = nameof(AssetsUpdated);

    /// <summary>Carries a new Alert, as <c>GET /alerts</c> shows it.</summary>
    public const string AlertRaised = nameof(AlertRaised);

    /// <summary>Carries an Alert that was Acknowledged or Resolved, as <c>GET /alerts</c> shows it.</summary>
    public const string AlertUpdated = nameof(AlertUpdated);
}
