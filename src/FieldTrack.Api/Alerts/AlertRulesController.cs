using FieldTrack.Api.Auth;
using FieldTrack.Domain.Alerts;
using FieldTrack.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FieldTrack.Api.Alerts;

/// <summary>
/// Alert Rules, which ingest evaluates against each new Reading (ADR 0005). Everyone signed in reads them;
/// only Admins change them.
/// </summary>
[ApiController]
[Route("alert-rules")]
[Authorize(Policy = Policies.Read)]
public sealed class AlertRulesController(FieldTrackDbContext db) : ControllerBase
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
    /// <response code="400">Not exactly one known target, an Archived Asset, or a Threshold rule missing a setting.</response>
    [HttpPost]
    [Authorize(Policy = Policies.Manage)]
    [ProducesResponseType<AlertRuleResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AlertRuleResponse>> Create(AlertRuleRequest request, CancellationToken cancellationToken)
    {
        if (await RefusalAsync(request, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        var rule = new AlertRule { Name = request.Name };
        Apply(request, rule);
        db.AlertRules.Add(rule);
        await db.SaveChangesAsync(cancellationToken);
        return StatusCode(StatusCodes.Status201Created, ToResponse(rule));
    }

    /// <summary>Edits an Alert Rule.</summary>
    /// <remarks>Resolving the rule's Active Alerts and restarting its rule state on an edit come with #34.</remarks>
    /// <response code="400">Not exactly one known target, an Archived Asset, or a Threshold rule missing a setting.</response>
    /// <response code="404">No Alert Rule has this id.</response>
    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.Manage)]
    [ProducesResponseType<AlertRuleResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AlertRuleResponse>> Update(Guid id, AlertRuleRequest request, CancellationToken cancellationToken)
    {
        if (await db.AlertRules.FindAsync([id], cancellationToken) is not { } rule)
        {
            return NotFound();
        }

        if (await RefusalAsync(request, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        Apply(request, rule);
        await db.SaveChangesAsync(cancellationToken);
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
        // An ingest batch that wrote an Alert or rule state for the rule holds a key share lock on it (the foreign
        // key) until it commits, so this lock waits for it and the check below then sees its Alert.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var rule = await db.AlertRules.FromSql($"SELECT * FROM alert_rules WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(cancellationToken);
        if (rule is null)
        {
            return NotFound();
        }

        if (await db.Alerts.AnyAsync(alert => alert.AlertRuleId == id, cancellationToken))
        {
            return Problem("The Alert Rule has raised Alerts; disable it instead.", statusCode: StatusCodes.Status409Conflict);
        }

        db.AlertRules.Remove(rule);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return NoContent();
    }

    private async Task<ActionResult?> RefusalAsync(AlertRuleRequest request, CancellationToken cancellationToken)
    {
        if ((request.AssetId is null) == (request.AssetGroupId is null))
        {
            ModelState.AddModelError(nameof(AlertRuleRequest.AssetId), "Give exactly one of assetId and assetGroupId.");
        }
        else if (request.AssetId is { } assetId && !await db.Assets.AnyAsync(asset => asset.Id == assetId && !asset.Archived, cancellationToken))
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
        rule.Sensor = request.Sensor;
        rule.Operator = request.Operator;
        rule.Threshold = request.Threshold;
        rule.Hysteresis = request.Hysteresis;
    }

    private static AlertRuleResponse ToResponse(AlertRule rule) =>
        new(rule.Id, rule.Name, rule.Kind, rule.AssetId, rule.AssetGroupId, rule.Severity, rule.Enabled,
            rule.Sensor, rule.Operator, rule.Threshold, rule.Hysteresis);
}
