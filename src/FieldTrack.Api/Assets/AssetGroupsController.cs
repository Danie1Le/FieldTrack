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
            .Select(group => new AssetGroupResponse(group.Id, group.Name))
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
        return StatusCode(StatusCodes.Status201Created, new AssetGroupResponse(group.Id, group.Name));
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
        return new AssetGroupResponse(group.Id, group.Name);
    }
}
