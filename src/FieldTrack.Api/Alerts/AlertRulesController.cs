using FieldTrack.Api.Auth;
using FieldTrack.Api.Live;
using FieldTrack.Domain.Alerts;
using FieldTrack.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FieldTrack.Api.Alerts;

/// <summary>
/// Alert Rules, which ingest evaluates against each new Reading (ADR 0005). Everyone signed in reads them;
/// only Admins change them. Every change bumps the Alert Rule version in its transaction, so ingest reloads the rules
/// before its next batch (ADR 0005).
/// </summary>
[ApiController]
[Route("alert-rules")]
[Authorize(Policy = Policies.Read)]
public sealed class AlertRulesController(FieldTrackDbContext db, TimeProvider time, LivePush live) : ControllerBase
{
    /// <summary>Lists Alert Rules by name, disabled ones included.</summary>
    [HttpGet]
    public async Task<IReadOnlyList<AlertRuleResponse>> List(CancellationToken cancellationToken) =>
        await db.AlertRules
            .OrderBy(rule => rule.Name)
            .Select(rule => ToResponse(rule))
            .ToListAsync(cancellationToken);

    /// <summary>Reads one Alert Rule.</summary>
    /// <response code="404">No Alert Rule has this id.</response>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<AlertRuleResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AlertRuleResponse>> Get(Guid id, CancellationToken cancellationToken) =>
        await db.AlertRules.FindAsync([id], cancellationToken) is { } rule ? ToResponse(rule) : NotFound();

    /// <summary>Creates an Alert Rule; ingest evaluates it from the next batch on.</summary>
    /// <response code="400">Not exactly one known target, an Archived Asset, a Threshold rule missing a setting, or a Geofence rule without a known Geofence.</response>
    [HttpPost]
    [Authorize(Policy = Policies.Manage)]
    [ProducesResponseType<AlertRuleResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AlertRuleResponse>> Create(AlertRuleRequest request, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockTargetAssetAsync(request, cancellationToken);
        // Bumped before the checks, so a Geofence delete that got there first has committed and is seen (ADR 0005).
        await db.BumpAlertRuleVersionAsync(cancellationToken);
        if (await RefusalAsync(request, currentAssetId: null, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        var rule = new AlertRule { Name = request.Name };
        Apply(request, rule);
        db.AlertRules.Add(rule);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return StatusCode(StatusCodes.Status201Created, ToResponse(rule));
    }

    /// <summary>
    /// Edits an Alert Rule. A rule whose Asset has been Archived can still be edited, so an Admin can disable it;
    /// only moving a rule onto an Archived Asset is refused.
    /// </summary>
    /// <remarks>
    /// An edit of the rule's condition (kind, target, threshold settings, Geofence, enabled) resolves its Active Alerts, as Rule
    /// disabled when it disables the rule and Rule changed otherwise, and drops its rule state, so the rule starts afresh
    /// on the next Reading (#10). A rename or a new severity leaves both as they are.
    /// </remarks>
    /// <response code="400">Not exactly one known target, a move to an Archived Asset, a Threshold rule missing a setting, or a Geofence rule without a known Geofence.</response>
    /// <response code="404">No Alert Rule has this id.</response>
    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.Manage)]
    [ProducesResponseType<AlertRuleResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AlertRuleResponse>> Update(Guid id, AlertRuleRequest request, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockTargetAssetAsync(request, cancellationToken);
        // The rule is read after the bump, so an Archive or another change of it that got there first has committed.
        await db.BumpAlertRuleVersionAsync(cancellationToken);
        if (await db.AlertRules.FindAsync([id], cancellationToken) is not { } rule)
        {
            return NotFound();
        }

        if (await RefusalAsync(request, rule.AssetId, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        var before = Condition(rule);
        Apply(request, rule);
        await db.SaveChangesAsync(cancellationToken);
        List<Guid> resolved = [];
        if (Condition(rule) != before)
        {
            var reason = rule.Enabled ? ResolveReason.RuleChanged : ResolveReason.RuleDisabled;
            resolved = await db.ResolveActiveAlertsOfRuleAsync(id, reason, time.GetUtcNow(), cancellationToken);
            await db.AlertRuleStates.Where(state => state.AlertRuleId == id).ExecuteDeleteAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        await live.AlertsUpdatedAsync(resolved, cancellationToken);
        return ToResponse(rule);
    }

    /// <summary>
    /// Deletes an Alert Rule that has never raised an Alert, with its rule state. A rule with Alerts is kept so their
    /// history keeps its rule; disable it instead.
    /// </summary>
    /// <response code="404">No Alert Rule has this id.</response>
    /// <response code="409">The rule has raised Alerts.</response>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Policies.Manage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        // Bumping the version waits for a running ingest batch, so the check below sees any Alert it raised for the rule,
        // and the next batch reloads without the rule instead of failing on its foreign key. Every other change of the
        // rule bumps the version first too, so the delete's row lock comes after it on every path (ADR 0005).
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.BumpAlertRuleVersionAsync(cancellationToken);
        if (!await db.AlertRules.AnyAsync(rule => rule.Id == id, cancellationToken))
        {
            return NotFound();
        }

        if (await db.Alerts.AnyAsync(alert => alert.AlertRuleId == id, cancellationToken))
        {
            return Problem("The Alert Rule has raised Alerts; disable it instead.", statusCode: StatusCodes.Status409Conflict);
        }

        await db.AlertRules.Where(rule => rule.Id == id).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return NoContent();
    }

    // Lock order (ADR 0005): the target Asset before the Alert Rule version, so the rule's foreign key check never takes
    // it after the version, and before the Archived check, so an Archive of it has committed or waits for this change.
    // Key share waits for an Archive or delete but not for ingest.
    private async Task LockTargetAssetAsync(AlertRuleRequest request, CancellationToken cancellationToken)
    {
        if (request.AssetId is { } assetId)
        {
            await db.Database.SqlQuery<Guid>($"""SELECT id AS "Value" FROM assets WHERE id = {assetId} FOR KEY SHARE""").ToListAsync(cancellationToken);
        }
    }

    // currentAssetId: the Asset an edited rule targets now, which may stay even once Archived.
    private async Task<ActionResult?> RefusalAsync(AlertRuleRequest request, Guid? currentAssetId, CancellationToken cancellationToken)
    {
        if ((request.AssetId is null) == (request.AssetGroupId is null))
        {
            ModelState.AddModelError(nameof(AlertRuleRequest.AssetId), "Give exactly one of assetId and assetGroupId.");
        }
        else if (request.AssetId is { } assetId && assetId != currentAssetId
                 && !await db.Assets.AnyAsync(asset => asset.Id == assetId && !asset.Archived, cancellationToken))
        {
            ModelState.AddModelError(nameof(AlertRuleRequest.AssetId), "No Asset in use has this id.");
        }
        else if (request.AssetGroupId is { } groupId && !await db.AssetGroups.AnyAsync(group => group.Id == groupId, cancellationToken))
        {
            ModelState.AddModelError(nameof(AlertRuleRequest.AssetGroupId), "No Asset Group has this id.");
        }

        if (request.Kind == AlertRuleKind.Threshold && (request.Sensor is null || request.Operator is null || request.Threshold is null || request.Hysteresis is null))
        {
            ModelState.AddModelError(nameof(AlertRuleRequest.Threshold), "A Threshold rule needs sensor, operator, threshold and hysteresis.");
        }

        if (IsGeofenceRule(request.Kind) && (request.GeofenceId is not { } geofenceId || !await db.Geofences.AnyAsync(geofence => geofence.Id == geofenceId, cancellationToken)))
        {
            ModelState.AddModelError(nameof(AlertRuleRequest.GeofenceId), "A Geofence rule needs the id of a Geofence.");
        }

        // The JSON options read "NaN" and "Infinity" from strings; such a threshold would never breach.
        if (request.Threshold is { } threshold && !double.IsFinite(threshold))
        {
            ModelState.AddModelError(nameof(AlertRuleRequest.Threshold), "The threshold must be a finite number.");
        }

        return ModelState.IsValid ? null : ValidationProblem(ModelState);
    }

    private static void Apply(AlertRuleRequest request, AlertRule rule)
    {
        rule.Name = request.Name;
        rule.Kind = request.Kind!.Value;
        rule.AssetId = request.AssetId;
        rule.AssetGroupId = request.AssetGroupId;
        rule.Severity = request.Severity!.Value;
        rule.Enabled = request.Enabled;
        // RefusalAsync has checked every setting, so the settings cannot refuse them.
        rule.ThresholdSettings = rule.Kind == AlertRuleKind.Threshold
            ? new ThresholdSettings(request.Sensor!.Value, request.Operator!.Value, request.Threshold!.Value, request.Hysteresis!.Value)
            : null;
        rule.GeofenceId = IsGeofenceRule(rule.Kind) ? request.GeofenceId : null;
    }

    private static bool IsGeofenceRule(AlertRuleKind? kind) => kind is AlertRuleKind.GeofenceExit or AlertRuleKind.GeofenceEnter;

    // What decides when the rule raises and resolves; editing any of it starts the rule afresh. Name and severity only
    // change how its Alerts read. A Geofence's own area is part of the condition too; GeofencesController handles that.
    private static (AlertRuleKind, Guid?, Guid?, ThresholdSettings?, Guid?, bool) Condition(AlertRule rule) =>
        (rule.Kind, rule.AssetId, rule.AssetGroupId, rule.ThresholdSettings, rule.GeofenceId, rule.Enabled);

    private static AlertRuleResponse ToResponse(AlertRule rule) =>
        new(rule.Id, rule.Name, rule.Kind, rule.AssetId, rule.AssetGroupId, rule.Severity, rule.Enabled,
            rule.ThresholdSettings?.Sensor, rule.ThresholdSettings?.Operator, rule.ThresholdSettings?.Threshold, rule.ThresholdSettings?.Hysteresis,
            rule.GeofenceId);
}
