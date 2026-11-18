using System.Buffers.Text;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using FieldTrack.Api.Auth;
using FieldTrack.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.Net.Http.Headers;
using Npgsql;

namespace FieldTrack.Api.Assets;

/// <summary>
/// CSV export of one Asset's Readings (#42). The dashboard asks for an export with its access token and gets a
/// single-use URL; the browser then downloads that URL with its own download UI, so the access token never appears in
/// a URL and the file never sits in browser memory. Admins and Operators both export.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.Read)]
public sealed class ExportsController(FieldTrackDbContext db, TimeProvider time) : ControllerBase
{
    /// <summary>How long an export URL works after it is issued (spec #21).</summary>
    public static readonly TimeSpan UrlLifetime = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Issues a single-use download URL, valid for <see cref="UrlLifetime"/>, for the Readings stamped with the Asset
    /// whose Device time is in [from, to). Archived Assets keep their history, so they export too.
    /// </summary>
    /// <response code="400">A missing bound, to not after from, or a range over 31 days (split it into several exports).</response>
    /// <response code="404">No Asset has this id.</response>
    /// <response code="429">The user already runs <see cref="ReadingCsvExport.MaxRunningPerUser"/> exports.</response>
    [HttpPost("assets/{assetId:guid}/exports")]
    [ProducesResponseType<ExportResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<ExportResponse>> Create(Guid assetId, ExportRequest request, CancellationToken cancellationToken)
    {
        // Npgsql writes timestamptz from UTC values only.
        var (from, to) = (request.From!.Value.ToUniversalTime(), request.To!.Value.ToUniversalTime());
        if (to <= from || to - from > ReadingsController.MaxRange)
        {
            ModelState.AddModelError(nameof(request.To),
                $"The range must end after it starts and span at most {ReadingsController.MaxRange.Days} days; split a longer range into several exports.");
            return ValidationProblem(ModelState);
        }

        if (!await db.Assets.AnyAsync(asset => asset.Id == assetId, cancellationToken))
        {
            return NotFound();
        }

        // Refused here too so the dashboard can say why; the download enforces the limit.
        var user = CurrentUserId();
        if (await ReadingCsvExport.RunningAsync(db, user, cancellationToken) >= ReadingCsvExport.MaxRunningPerUser)
        {
            return TooManyRunning();
        }

        var token = RandomNumberGenerator.GetBytes(32);
        var expiresAt = time.GetUtcNow() + UrlLifetime;
        db.ExportTickets.Add(new ExportTicket
        {
            TokenHash = SHA256.HashData(token),
            UserId = user,
            AssetId = assetId,
            RangeStart = from,
            RangeEnd = to,
            ExpiresAt = expiresAt,
        });
        await db.SaveChangesAsync(cancellationToken);
        return new ExportResponse($"/exports?token={Base64Url.EncodeToString(token)}", expiresAt);
    }

    /// <summary>
    /// Downloads an export as CSV, streamed from the database as it is read (chunked, no length), named after the Asset
    /// and range. The URL is the credential: it works once, within <see cref="UrlLifetime"/> of being issued.
    /// </summary>
    /// <response code="404">An unknown, used or expired URL.</response>
    /// <response code="429">The user who asked for it already runs <see cref="ReadingCsvExport.MaxRunningPerUser"/> exports; the URL stays unused.</response>
    [HttpGet("exports")]
    [AllowAnonymous]
    [Produces("text/csv")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Download(string token, CancellationToken cancellationToken)
    {
        if (!Base64Url.IsValid(token))
        {
            return NotFound();
        }

        var hash = SHA256.HashData(Base64Url.DecodeFromChars(token));
        if (await db.ExportTickets.AsNoTracking().SingleOrDefaultAsync(ticket => ticket.TokenHash == hash, cancellationToken) is not { } ticket)
        {
            return NotFound();
        }

        // The slot is a lock on this session, so the slot, the redeem and the COPY share one open connection.
        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await using var slot = await ReadingCsvExport.TryStartAsync(connection, ticket.UserId, cancellationToken);
        if (slot is null)
        {
            return TooManyRunning();
        }

        // Redeemed with a conditional update, so of two concurrent downloads only one gets the file.
        var now = time.GetUtcNow();
        if (await db.ExportTickets
                .Where(t => t.Id == ticket.Id && !t.Used && t.ExpiresAt > now)
                .ExecuteUpdateAsync(set => set.SetProperty(t => t.Used, true), cancellationToken) == 0)
        {
            return NotFound();
        }

        var assetName = await db.Assets.Where(asset => asset.Id == ticket.AssetId).Select(asset => asset.Name).SingleAsync(cancellationToken);
        var disposition = new ContentDispositionHeaderValue("attachment");
        disposition.SetHttpFileName(FileName(assetName, ticket.RangeStart, ticket.RangeEnd));
        Response.ContentType = "text/csv; charset=utf-8";
        Response.Headers.ContentDisposition = disposition.ToString();
        await ReadingCsvExport.CopyAsync(connection, ticket.AssetId, ticket.RangeStart, ticket.RangeEnd, Response.Body, cancellationToken);
        return new EmptyResult();
    }

    /// <summary><c>&lt;asset-name&gt;_&lt;from&gt;_&lt;to&gt;.csv</c> (spec #21), times in ISO 8601 basic format so the name holds no colon.</summary>
    private static string FileName(string assetName, DateTimeOffset from, DateTimeOffset to)
    {
        // Characters a file name may not hold on Windows, macOS or Linux become _.
        var name = string.Concat(assetName.Select(c => char.IsControl(c) || "\\/:*?\"<>|".Contains(c) ? '_' : c));
        return string.Create(CultureInfo.InvariantCulture, $"{name}_{from.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}_{to.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}.csv");
    }

    private ObjectResult TooManyRunning() =>
        Problem($"You already run {ReadingCsvExport.MaxRunningPerUser} exports; wait for one to finish.", statusCode: StatusCodes.Status429TooManyRequests);

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(JwtRegisteredClaimNames.Sub)!);
}
