using FieldTrack.Api.Auth;
using FieldTrack.Api.Live;
using FieldTrack.Domain.Alerts;
using FieldTrack.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite;
using NetTopologySuite.Geometries;

namespace FieldTrack.Api.Alerts;

/// <summary>
/// Geofences, named areas that Geofence rules watch. Everyone signed in reads them; only Admins change them. An edit or
/// delete bumps the Alert Rule version in its transaction, so ingest reloads the Geofences with the rules before its
/// next batch (ADR 0005).
/// </summary>
[ApiController]
[Route("geofences")]
[Authorize(Policy = Policies.Read)]
public sealed class GeofencesController(FieldTrackDbContext db, TimeProvider time, LivePush live) : ControllerBase
{
    /// <summary>
    /// The most positions an area may have over all its rings. Ingest checks every Reading of every Geofence rule
    /// against the area, so its size bounds the work each Reading costs.
    /// </summary>
    public const int MaxPositions = 1000;

    private static readonly GeometryFactory _wgs84 = NtsGeometryServices.Instance.CreateGeometryFactory(4326);

    /// <summary>Lists Geofences by name.</summary>
    [HttpGet]
    public async Task<IReadOnlyList<GeofenceResponse>> List(CancellationToken cancellationToken) =>
        [.. (await db.Geofences.OrderBy(geofence => geofence.Name).ToListAsync(cancellationToken)).Select(ToResponse)];

    /// <summary>Reads one Geofence.</summary>
    /// <response code="404">No Geofence has this id.</response>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<GeofenceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<GeofenceResponse>> Get(Guid id, CancellationToken cancellationToken) =>
        await db.Geofences.FindAsync([id], cancellationToken) is { } geofence ? ToResponse(geofence) : NotFound();

    /// <summary>Creates a Geofence. It affects no Asset until an Alert Rule names it.</summary>
    /// <response code="400">A missing name or kind, or an area that is not one valid polygon of at most <see cref="MaxPositions"/> positions.</response>
    [HttpPost]
    [Authorize(Policy = Policies.Manage)]
    [ProducesResponseType<GeofenceResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<GeofenceResponse>> Create(GeofenceRequest request, CancellationToken cancellationToken)
    {
        if (ToPolygon(request.Area!) is not { } area)
        {
            return ValidationProblem(ModelState);
        }

        // No version bump: no rule names a new Geofence yet, so ingest has nothing to reload.
        var geofence = new Geofence { Name = request.Name, Kind = request.Kind!.Value, Area = area };
        db.Geofences.Add(geofence);
        await db.SaveChangesAsync(cancellationToken);
        return StatusCode(StatusCodes.Status201Created, ToResponse(geofence));
    }

    /// <summary>Edits a Geofence.</summary>
    /// <remarks>
    /// A new area is a new condition for every rule that names the Geofence: their Active Alerts are resolved as Rule
    /// changed and their rule state dropped, so the next Reading sets a fresh baseline (ADR 0005). A new name or kind
    /// leaves both as they are.
    /// </remarks>
    /// <response code="400">A missing name or kind, or an area that is not one valid polygon of at most <see cref="MaxPositions"/> positions.</response>
    /// <response code="404">No Geofence has this id.</response>
    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.Manage)]
    [ProducesResponseType<GeofenceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<GeofenceResponse>> Update(Guid id, GeofenceRequest request, CancellationToken cancellationToken)
    {
        if (ToPolygon(request.Area!) is not { } area)
        {
            return ValidationProblem(ModelState);
        }

        // Lock order (ADR 0005): the version before the Geofence row and the rules, Alerts and rule state after it.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.BumpAlertRuleVersionAsync(cancellationToken);
        if (await db.Geofences.FindAsync([id], cancellationToken) is not { } geofence)
        {
            return NotFound();
        }

        var moved = !geofence.Area.EqualsExact(area);
        (geofence.Name, geofence.Kind, geofence.Area) = (request.Name, request.Kind!.Value, area);
        await db.SaveChangesAsync(cancellationToken);
        List<Guid> resolved = [];
        if (moved)
        {
            var rules = await db.AlertRules.Where(rule => rule.GeofenceId == id).Select(rule => rule.Id).ToListAsync(cancellationToken);
            foreach (var rule in rules)
            {
                resolved.AddRange(await db.ResolveActiveAlertsOfRuleAsync(rule, ResolveReason.RuleChanged, time.GetUtcNow(), cancellationToken));
            }

            await db.AlertRuleStates.Where(state => rules.Contains(state.AlertRuleId)).ExecuteDeleteAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        await live.AlertsUpdatedAsync(resolved, cancellationToken);
        return ToResponse(geofence);
    }

    /// <summary>Deletes a Geofence that no Alert Rule names.</summary>
    /// <response code="404">No Geofence has this id.</response>
    /// <response code="409">An Alert Rule names the Geofence.</response>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Policies.Manage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        // Rule changes bump the version before they check their Geofence, so after this bump a rule naming it has either
        // committed, and the check below sees it, or waits and then finds the Geofence gone.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.BumpAlertRuleVersionAsync(cancellationToken);
        if (!await db.Geofences.AnyAsync(geofence => geofence.Id == id, cancellationToken))
        {
            return NotFound();
        }

        if (await db.AlertRules.AnyAsync(rule => rule.GeofenceId == id, cancellationToken))
        {
            return Problem("An Alert Rule names the Geofence; delete or change that rule first.", statusCode: StatusCodes.Status409Conflict);
        }

        await db.Geofences.Where(geofence => geofence.Id == id).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return NoContent();
    }

    // The area as a WGS 84 polygon, or null with the reasons in ModelState.
    private Polygon? ToPolygon(GeoJsonPolygon area)
    {
        var rings = area.Coordinates!;
        if (area.Type != "Polygon" || rings.Length == 0 || rings.Any(ring => ring is null || ring.Any(position => position is null)))
        {
            ModelState.AddModelError(nameof(GeofenceRequest.Area), "The area must be a GeoJSON Polygon with at least one ring.");
            return null;
        }

        if (rings.Sum(ring => ring.Length) > MaxPositions)
        {
            ModelState.AddModelError(nameof(GeofenceRequest.Area), $"The area may have at most {MaxPositions} positions.");
            return null;
        }

        // The JSON options read "NaN" and "Infinity" from strings, which the range checks refuse too.
        if (rings.Any(ring => ring.Any(position => position is not [>= -180 and <= 180, >= -90 and <= 90])))
        {
            ModelState.AddModelError(nameof(GeofenceRequest.Area), "Each position must be [longitude, latitude] with longitude -180 to 180 and latitude -90 to 90.");
            return null;
        }

        if (rings.Any(ring => ring.Length < 4 || ring[0][0] != ring[^1][0] || ring[0][1] != ring[^1][1]))
        {
            ModelState.AddModelError(nameof(GeofenceRequest.Area), "Each ring must be closed (last position equals the first) and have at least 4 positions.");
            return null;
        }

        var linearRings = rings.Select(ring => _wgs84.CreateLinearRing([.. ring.Select(position => new Coordinate(position[0], position[1]))])).ToArray();
        var polygon = _wgs84.CreatePolygon(linearRings[0], linearRings[1..]);
        if (!polygon.IsValid)
        {
            ModelState.AddModelError(nameof(GeofenceRequest.Area), "The area must be a valid polygon: rings may not cross themselves or each other.");
            return null;
        }

        return polygon;
    }

    private static GeofenceResponse ToResponse(Geofence geofence) =>
        new(geofence.Id, geofence.Name, geofence.Kind, new GeoJsonPolygon("Polygon",
            [.. new[] { geofence.Area.Shell }.Concat(geofence.Area.Holes).Select(ring => ring.Coordinates.Select(c => new[] { c.X, c.Y }).ToArray())]));
}
