using FieldTrack.Api.Auth;
using FieldTrack.Domain.Alerts;
using FieldTrack.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FieldTrack.Api.Alerts;

/// <summary>Alerts raised by ingest (ADR 0005), Active and Resolved. Everyone signed in reads them.</summary>
[ApiController]
[Route("alerts")]
[Authorize(Policy = Policies.Read)]
public sealed class AlertsController(FieldTrackDbContext db) : ControllerBase
{
    // The most Alerts one list returns.
    // shortcut: newest only, no paging; add paging when the Alert feed (#37) pages back through history.
    private const int MaxAlerts = 500;

    /// <summary>
    /// Lists the newest Alerts by Device time raised, newest first: only Active ones when <paramref name="active"/> is
    /// true, only Resolved ones when false, both when left out.
    /// </summary>
    [HttpGet]
    public async Task<IReadOnlyList<AlertResponse>> List(bool? active, CancellationToken cancellationToken)
    {
        var alerts = db.Alerts.AsQueryable();
        if (active is { } isActive)
        {
            alerts = alerts.Where(alert => (alert.Status != AlertStatus.Resolved) == isActive);
        }

        return await alerts
            .OrderByDescending(alert => alert.RaisedAt)
            .ThenByDescending(alert => alert.Id)
            .Take(MaxAlerts)
            .Select(alert => ToResponse(alert))
            .ToListAsync(cancellationToken);
    }

    /// <summary>Reads one Alert.</summary>
    /// <response code="404">No Alert has this id.</response>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<AlertResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AlertResponse>> Get(Guid id, CancellationToken cancellationToken) =>
        await db.Alerts.FindAsync([id], cancellationToken) is { } alert ? ToResponse(alert) : NotFound();

    private static AlertResponse ToResponse(Alert alert) =>
        new(alert.Id, alert.AlertRuleId, alert.AssetId, alert.Reading, alert.Severity, alert.Message, alert.Status,
            alert.RaisedAt, alert.ResolvedAt, alert.ResolveReason, alert.CreatedAt, alert.FromBacklog);
}
