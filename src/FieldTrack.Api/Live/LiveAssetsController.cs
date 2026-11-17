using FieldTrack.Api.Auth;
using FieldTrack.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FieldTrack.Api.Live;

/// <summary>The dashboard's starting point, and how it recovers a push it missed (ADR 0005). Everyone signed in reads it.</summary>
[ApiController]
[Route("assets/live")]
[Authorize(Policy = Policies.Read)]
public sealed class LiveAssetsController(FieldTrackDbContext db, TimeProvider time) : ControllerBase
{
    /// <summary>
    /// Lists the Assets not Archived, by name, each with its Asset Status, its latest position and sensor values, and
    /// when its Device was last heard from. <see cref="LiveHub.AssetsUpdated"/> pushes the same rows as they change.
    /// </summary>
    [HttpGet]
    public Task<List<LiveAssetResponse>> List(CancellationToken cancellationToken) =>
        LiveAssets.ListAsync(db, ids: null, time.GetUtcNow(), cancellationToken);
}
