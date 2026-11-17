using FieldTrack.Api.Auth;
using FieldTrack.Domain.Assets;
using FieldTrack.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FieldTrack.Api.Assets;

/// <summary>
/// Asset Groups, such as a site or fleet. Assets join and leave a group through their own
/// <c>assetGroupId</c>. Everyone signed in reads groups; only Admins change them (spec #21, Auth).
/// </summary>
[ApiController]
[Route("asset-groups")]
[Authorize(Policy = Policies.Read)]
public sealed class AssetGroupsController(FieldTrackDbContext db) : ControllerBase
{
    /// <summary>Lists Asset Groups by name.</summary>
    [HttpGet]
    public async Task<IReadOnlyList<AssetGroupResponse>> List(CancellationToken cancellationToken) =>
        await db.AssetGroups
            .OrderBy(group => group.Name)
            .Select(group => ToResponse(group))
            .ToListAsync(cancellationToken);

    /// <summary>Creates an Asset Group.</summary>
    /// <response code="400">A missing name.</response>
    [HttpPost]
    [Authorize(Policy = Policies.Manage)]
    [ProducesResponseType<AssetGroupResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AssetGroupResponse>> Create(AssetGroupRequest request, CancellationToken cancellationToken)
    {
        var group = new AssetGroup { Name = request.Name };
        db.AssetGroups.Add(group);
        await db.SaveChangesAsync(cancellationToken);
        return StatusCode(StatusCodes.Status201Created, ToResponse(group));
    }

    /// <summary>Renames an Asset Group.</summary>
    /// <response code="400">A missing name.</response>
    /// <response code="404">No Asset Group has this id.</response>
    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.Manage)]
    [ProducesResponseType<AssetGroupResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AssetGroupResponse>> Update(Guid id, AssetGroupRequest request, CancellationToken cancellationToken)
    {
        if (await db.AssetGroups.FindAsync([id], cancellationToken) is not { } group)
        {
            return NotFound();
        }

        group.Name = request.Name;
        await db.SaveChangesAsync(cancellationToken);
        return ToResponse(group);
    }

    /// <summary>
    /// Deletes an Asset Group that no Asset (Archived ones too, whose history keeps their group) and no Alert Rule
    /// names, so a mistaken group is easy to clean up.
    /// </summary>
    /// <response code="404">No Asset Group has this id.</response>
    /// <response code="409">An Asset or an Alert Rule names the group.</response>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Policies.Manage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        // Rule changes bump the version before they check their group, so after this bump a rule naming it has either
        // committed, and the check below sees it, or waits and then finds the group gone (ADR 0005). The row lock waits
        // for an Asset joining the group, whose foreign key check holds the row, so the check below sees that Asset too.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.BumpAlertRuleVersionAsync(cancellationToken);
        if ((await db.Database.SqlQuery<Guid>($"""SELECT id AS "Value" FROM asset_groups WHERE id = {id} FOR UPDATE""").ToListAsync(cancellationToken)).Count == 0)
        {
            return NotFound();
        }

        if (await db.Assets.AnyAsync(asset => asset.AssetGroupId == id, cancellationToken)
            || await db.AlertRules.AnyAsync(rule => rule.AssetGroupId == id, cancellationToken))
        {
            return Problem("Assets or Alert Rules name the Asset Group; move or change them first.", statusCode: StatusCodes.Status409Conflict);
        }

        await db.AssetGroups.Where(group => group.Id == id).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return NoContent();
    }

    private static AssetGroupResponse ToResponse(AssetGroup group) => new(group.Id, group.Name);
}
