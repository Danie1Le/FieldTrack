using FieldTrack.Api.Alerts;
using FieldTrack.Api.Ingest;
using FieldTrack.Domain.Alerts;
using FieldTrack.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace FieldTrack.Api.Live;

/// <summary>
/// Sends committed changes to every dashboard on <see cref="LiveHub"/>. Call it only after the change commits
/// (ADR 0005: push after commit), so a dashboard never shows what a rollback undid. The pushes that carry rows
/// read the current rows themselves, so they always match what <c>GET /assets/live</c> and <c>GET /alerts</c>
/// return; <see cref="GeofencesChangedAsync"/> carries nothing and only tells dashboards to reload
/// <c>GET /geofences</c>. It never throws: a
/// failed push is logged and only delays the dashboard until it reloads (ADR 0005), so callers whose change already
/// committed never fail because of it.
/// </summary>
public sealed partial class LivePush(IHubContext<LiveHub> hub, IServiceScopeFactory scopes, TimeProvider time, IngestMetrics metrics, ILogger<LivePush> logger)
{
    /// <summary>
    /// Sends the Alerts <paramref name="raisedAlertIds"/> as <see cref="LiveHub.AlertRaised"/> and
    /// <paramref name="updatedAlertIds"/> as <see cref="LiveHub.AlertUpdated"/>, then the Assets
    /// <paramref name="assetIds"/> together with every Asset those Alerts are about, whose Asset Status they may have
    /// changed. A raised Alert that is already Resolved (raised and resolved in the same commit) goes as
    /// <see cref="LiveHub.AlertUpdated"/>, so <see cref="LiveHub.AlertRaised"/> always means a new Active Alert.
    /// </summary>
    public async Task PushAsync(
        IReadOnlyCollection<Guid> assetIds, IReadOnlyCollection<Guid> raisedAlertIds, IReadOnlyCollection<Guid> updatedAlertIds,
        CancellationToken cancellationToken)
    {
        if (assetIds.Count == 0 && raisedAlertIds.Count == 0 && updatedAlertIds.Count == 0)
        {
            return;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<FieldTrackDbContext>();
            List<Guid> alertIds = [.. raisedAlertIds, .. updatedAlertIds];
            var alerts = alertIds.Count == 0
                ? []
                : await AlertsController.Responses(db, db.Alerts
                        .Where(alert => alertIds.Contains(alert.Id))
                        .OrderBy(alert => alert.CreatedAt))
                    .ToListAsync(cancellationToken);

            // Alerts first, so the Asset read adds nothing to their latency.
            foreach (var alert in alerts)
            {
                var raised = raisedAlertIds.Contains(alert.Id) && alert.Status != AlertStatus.Resolved;
                await hub.Clients.All.SendAsync(raised ? LiveHub.AlertRaised : LiveHub.AlertUpdated, alert, cancellationToken);
                if (raised)
                {
                    metrics.RecordAlertPush(time.GetUtcNow() - alert.CreatedAt);
                }
            }

            var assets = await LiveAssets.ListAsync(db, [.. assetIds.Union(alerts.Select(alert => alert.AssetId))], time.GetUtcNow(), cancellationToken);

            if (assets.Count > 0)
            {
                await hub.Clients.All.SendAsync(LiveHub.AssetsUpdated, assets, cancellationToken);
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogPushFailed(exception);
        }
    }

    /// <summary>Sends an Alert a user Acknowledged or Resolved, and its Asset; call it once that change commits.</summary>
    public Task AlertUpdatedAsync(Guid alertId, CancellationToken cancellationToken) =>
        PushAsync([], [], [alertId], cancellationToken);

    /// <summary>Sends Alerts the platform resolved outside ingest, and their Assets; call it once that change commits.</summary>
    public Task AlertsUpdatedAsync(IReadOnlyCollection<Guid> alertIds, CancellationToken cancellationToken) =>
        PushAsync([], [], alertIds, cancellationToken);

    /// <summary>Sends <see cref="LiveHub.GeofencesChanged"/>; call it once a Geofence create, edit or delete commits.</summary>
    public async Task GeofencesChangedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await hub.Clients.All.SendAsync(LiveHub.GeofencesChanged, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogPushFailed(exception);
        }
    }

    [LoggerMessage(LogLevel.Warning, "Live push failed; dashboards catch up when they reload.")]
    private partial void LogPushFailed(Exception exception);
}
