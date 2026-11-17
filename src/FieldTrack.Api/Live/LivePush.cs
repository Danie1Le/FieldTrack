using FieldTrack.Api.Alerts;
using FieldTrack.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace FieldTrack.Api.Live;

/// <summary>
/// Sends committed changes to every dashboard on <see cref="LiveHub"/>. Call it only after the change commits
/// (ADR 0005: push after commit), so a dashboard never shows what a rollback undid. It reads the current rows
/// itself, so a push always matches what <c>GET /assets/live</c> and <c>GET /alerts</c> return.
/// </summary>
public sealed class LivePush(IHubContext<LiveHub> hub, IServiceScopeFactory scopes, TimeProvider time)
{
    /// <summary>
    /// Sends the Alerts <paramref name="raisedAlertIds"/> as raised and <paramref name="updatedAlertIds"/> as updated,
    /// then the Assets <paramref name="assetIds"/> together with every Asset those Alerts are about, whose Asset Status
    /// they may have changed.
    /// </summary>
    public async Task PushAsync(
        IReadOnlyCollection<Guid> assetIds, IReadOnlyCollection<Guid> raisedAlertIds, IReadOnlyCollection<Guid> updatedAlertIds,
        CancellationToken cancellationToken)
    {
        if (assetIds.Count == 0 && raisedAlertIds.Count == 0 && updatedAlertIds.Count == 0)
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FieldTrackDbContext>();
        List<Guid> alertIds = [.. raisedAlertIds, .. updatedAlertIds];
        var alerts = await db.Alerts.AsNoTracking()
            .Where(alert => alertIds.Contains(alert.Id))
            .OrderBy(alert => alert.CreatedAt)
            .ToListAsync(cancellationToken);
        var assets = await LiveAssets.ListAsync(db, [.. assetIds.Union(alerts.Select(alert => alert.AssetId))], time.GetUtcNow(), cancellationToken);

        foreach (var alert in alerts)
        {
            var name = raisedAlertIds.Contains(alert.Id) ? LiveHub.AlertRaised : LiveHub.AlertUpdated;
            await hub.Clients.All.SendAsync(name, AlertsController.ToResponse(alert), cancellationToken);
        }

        if (assets.Count > 0)
        {
            await hub.Clients.All.SendAsync(LiveHub.AssetsUpdated, assets, cancellationToken);
        }
    }

    /// <summary>Sends an Alert a user Acknowledged or Resolved, and its Asset; call it once that change commits.</summary>
    public Task AlertUpdatedAsync(Guid alertId, CancellationToken cancellationToken) =>
        PushAsync([], [], [alertId], cancellationToken);
}
