using FieldTrack.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace FieldTrack.Api.Live;

/// <summary>
/// The SignalR hub dashboards listen on for live changes; <see cref="LivePush"/> sends them after each change commits.
/// Clients only listen, so it has no methods. Pushes carry no delivery guarantee: a dashboard that missed one, or
/// reconnects, reloads <c>GET /assets/live</c>, <c>GET /alerts</c>, <c>GET /geofences</c>, <c>GET /alert-rules</c> and
/// <c>GET /asset-groups</c> (ADR 0005).
/// </summary>
[Authorize(Policy = Policies.Read)]
public sealed class LiveHub : Hub
{
    /// <summary>Where the hub is mapped; under the hubs path, so the access token may come in the <c>access_token</c> query parameter.</summary>
    public const string Path = AuthServiceCollectionExtensions.HubsPath + "/live";

    /// <summary>Carries the <see cref="LiveAssetResponse"/> of every Asset a commit changed, one message per commit.</summary>
    public const string AssetsUpdated = nameof(AssetsUpdated);

    /// <summary>
    /// Carries the ids of Assets that are no longer on the map or in lists, because they were Archived or deleted
    /// (GLOSSARY Archived), one message per commit.
    /// </summary>
    public const string AssetsRemoved = nameof(AssetsRemoved);

    /// <summary>Carries a new Alert, as <c>GET /alerts</c> shows it.</summary>
    public const string AlertRaised = nameof(AlertRaised);

    /// <summary>Carries an Alert that was Acknowledged or Resolved, as <c>GET /alerts</c> shows it.</summary>
    public const string AlertUpdated = nameof(AlertUpdated);

    /// <summary>
    /// Carries nothing: a Geofence was created, edited or deleted, so dashboards reload <c>GET /geofences</c>. Edits are
    /// rare and the list is small, so a reload is simpler than sending the change itself.
    /// </summary>
    public const string GeofencesChanged = nameof(GeofencesChanged);

    /// <summary>Carries nothing: an Alert Rule was created, edited or deleted, so dashboards reload <c>GET /alert-rules</c>, as for <see cref="GeofencesChanged"/>.</summary>
    public const string AlertRulesChanged = nameof(AlertRulesChanged);

    /// <summary>Carries nothing: an Asset Group was created, renamed or deleted, so dashboards reload <c>GET /asset-groups</c>, as for <see cref="GeofencesChanged"/>.</summary>
    public const string AssetGroupsChanged = nameof(AssetGroupsChanged);
}
