using FieldTrack.Api.Auth;
using FieldTrack.Domain.Telemetry;
using FieldTrack.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FieldTrack.Api.Telemetry;

/// <summary>
/// Rejected messages: what the telemetry ingest refused, listed so an Admin can see why a Device's Readings are
/// missing. Admins only (see <see cref="Policies"/>): the payloads come from anyone who can publish to the broker,
/// not only from registered Devices.
/// </summary>
[ApiController]
[Route("rejected-messages")]
[Authorize(Policy = Policies.Manage)]
public sealed class RejectedMessagesController(FieldTrackDbContext db) : ControllerBase
{
    /// <summary>
    /// The most Rejected messages one request returns; a misbehaving Device can produce them without end. Each is at most
    /// a 4 KiB payload (the broker's message_size_limit) and a <see cref="RejectedMessage.MaxTopicLength"/>-character topic.
    /// </summary>
    public const int PageSize = 100;

    /// <summary>
    /// Lists the latest Rejected messages, newest first. Pass the last <c>id</c> of a page as <paramref name="before"/>
    /// to get the older ones after it.
    /// </summary>
    [HttpGet]
    public async Task<IReadOnlyList<RejectedMessageResponse>> List(long? before, CancellationToken cancellationToken) =>
        await db.RejectedMessages
            .Where(message => before == null || message.Id < before)
            .OrderByDescending(message => message.Id)
            .Take(PageSize)
            .Select(message => new RejectedMessageResponse(
                message.Id, message.Topic, message.TopicClipped, message.Payload, message.Reason, message.ReceivedTime))
            .ToListAsync(cancellationToken);
}
