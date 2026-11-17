using System.Linq.Expressions;
using System.Security.Claims;
using FieldTrack.Api.Auth;
using FieldTrack.Domain.Alerts;
using FieldTrack.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.IdentityModel.JsonWebTokens;

namespace FieldTrack.Api.Alerts;

/// <summary>
/// Alerts raised by ingest (ADR 0005), Active and Resolved. Everyone signed in reads them; Admins and Operators
/// Acknowledge and Resolve them.
/// </summary>
[ApiController]
[Route("alerts")]
[Authorize(Policy = Policies.Read)]
public sealed class AlertsController(FieldTrackDbContext db, TimeProvider time) : ControllerBase
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

        return await Responses(alerts
                .OrderByDescending(alert => alert.RaisedAt)
                .ThenByDescending(alert => alert.Id)
                .Take(MaxAlerts))
            .ToListAsync(cancellationToken);
    }

    /// <summary>Reads one Alert.</summary>
    /// <response code="404">No Alert has this id.</response>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<AlertResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AlertResponse>> Get(Guid id, CancellationToken cancellationToken) =>
        await ReadAsync(id, cancellationToken) is { } alert ? alert : NotFound();

    /// <summary>Acknowledges an Open Alert as the signed-in user, without ending it.</summary>
    /// <response code="404">No Alert has this id.</response>
    /// <response code="409">The Alert is not Open: already Acknowledged or Resolved.</response>
    [HttpPost("{id:guid}/acknowledge")]
    [Authorize(Policy = Policies.AlertActions)]
    [ProducesResponseType<AlertResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AlertResponse>> Acknowledge(Guid id, CancellationToken cancellationToken)
    {
        var user = CurrentUserId();
        var now = time.GetUtcNow();
        // Conditional, so an Alert the platform Resolves at the same moment is not Acknowledged (#10).
        var acknowledged = await db.Alerts
            .Where(alert => alert.Id == id && alert.Status == AlertStatus.Open)
            .ExecuteUpdateAsync(set => set
                .SetProperty(alert => alert.Status, AlertStatus.Acknowledged)
                .SetProperty(alert => alert.AcknowledgedBy, user)
                .SetProperty(alert => alert.AcknowledgedAt, now), cancellationToken);
        return await OutcomeAsync(id, acknowledged, "Only an Open Alert can be Acknowledged.", cancellationToken);
    }

    /// <summary>
    /// Resolves an Active Alert by hand as the signed-in user, with an optional note. If its condition still holds,
    /// the rule latches: it raises nothing new for the Asset until the condition clears and breaches again, and the
    /// response says so in <see cref="AlertResponse.Latched"/>.
    /// </summary>
    /// <response code="400">The note is longer than <see cref="Alert.MaxNoteLength"/>.</response>
    /// <response code="404">No Alert has this id.</response>
    /// <response code="409">The Alert is already Resolved, by the platform or another user.</response>
    [HttpPost("{id:guid}/resolve")]
    [Authorize(Policy = Policies.AlertActions)]
    [ProducesResponseType<AlertResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AlertResponse>> Resolve(
        Guid id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ResolveAlertRequest? request,
        CancellationToken cancellationToken)
    {
        var user = CurrentUserId();
        var now = time.GetUtcNow();
        var note = string.IsNullOrWhiteSpace(request?.Note) ? null : request.Note;
        // ADR 0005: a conditional update like ingest's, so whichever of the platform and users Resolves first wins and
        // every later attempt changes nothing. The latch is read from the rule state ingest keeps; resolving leaves
        // that state alone, which is what keeps the rule from raising again while the condition holds.
        var resolved = await db.Alerts
            .Where(alert => alert.Id == id && alert.Status != AlertStatus.Resolved)
            .ExecuteUpdateAsync(set => set
                .SetProperty(alert => alert.Status, AlertStatus.Resolved)
                .SetProperty(alert => alert.ResolvedAt, now)
                .SetProperty(alert => alert.ResolveReason, ResolveReason.Manual)
                .SetProperty(alert => alert.ResolvedBy, user)
                .SetProperty(alert => alert.Note, note)
                .SetProperty(alert => alert.Latched, ConditionHolds()), cancellationToken);
        return await OutcomeAsync(id, resolved, "The Alert is already Resolved.", cancellationToken);
    }

    // The JWT's sub claim is the user id (TokenService).
    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(JwtRegisteredClaimNames.Sub)!);

    private async Task<ActionResult<AlertResponse>> OutcomeAsync(Guid id, int changed, string refusal, CancellationToken cancellationToken) =>
        await ReadAsync(id, cancellationToken) switch
        {
            null => NotFound(),
            _ when changed == 0 => Problem(refusal, statusCode: StatusCodes.Status409Conflict),
            var alert => alert,
        };

    private Task<AlertResponse?> ReadAsync(Guid id, CancellationToken cancellationToken) =>
        Responses(db.Alerts.Where(alert => alert.Id == id)).SingleOrDefaultAsync(cancellationToken);

    // The Latch (GLOSSARY): whether the Alert's condition holds now, from the rule state ingest keeps. Resolve stores it
    // and responses show it for Active Alerts, so both use this one definition.
    private Expression<Func<Alert, bool>> ConditionHolds() =>
        alert => db.AlertRuleStates.Any(state => state.AlertRuleId == alert.AlertRuleId && state.AssetId == alert.AssetId && state.Breached);

    // Users are named by display name, never email: every signed-in user reads Alerts, the Demo Operator included,
    // and only Admins see emails (/users).
    private IQueryable<AlertResponse> Responses(IQueryable<Alert> alerts)
    {
        Expression<Func<Alert, bool, AlertResponse>> response = (alert, conditionHolds) => new AlertResponse(
            alert.Id, alert.AlertRuleId, alert.AssetId, alert.Reading, alert.Severity, alert.Message, alert.Status,
            alert.RaisedAt, alert.ResolvedAt, alert.ResolveReason,
            db.Users.Where(user => user.Id == alert.AcknowledgedBy).Select(user => user.DisplayName).SingleOrDefault(),
            alert.AcknowledgedAt,
            db.Users.Where(user => user.Id == alert.ResolvedBy).Select(user => user.DisplayName).SingleOrDefault(),
            alert.Note,
            // Active: whether the condition holds now. Resolved: whether it held when resolved by hand.
            alert.Status == AlertStatus.Resolved ? alert.Latched : conditionHolds,
            alert.CreatedAt, alert.FromBacklog);
        // Splices ConditionHolds in for conditionHolds, so EF runs it as a subquery of this query.
        var holds = ConditionHolds();
        var alertParameter = response.Parameters[0];
        var body = ReplacingExpressionVisitor.Replace(
            response.Parameters[1], ReplacingExpressionVisitor.Replace(holds.Parameters[0], alertParameter, holds.Body), response.Body);
        return alerts.Select(Expression.Lambda<Func<Alert, AlertResponse>>(body, alertParameter));
    }
}
