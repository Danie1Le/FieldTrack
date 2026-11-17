using FieldTrack.Domain.Telemetry;

namespace FieldTrack.Api.Telemetry;

/// <summary>
/// A Rejected message as an Admin sees it. <paramref name="Topic"/> and <paramref name="Payload"/> are untrusted: whoever
/// published the message chose them, so a client shows them as text only, never as markup.
/// </summary>
/// <param name="Id">The Rejected message's id; ids rise in the order messages were rejected.</param>
/// <param name="Topic">The MQTT topic it arrived on, at most <see cref="RejectedMessage.MaxTopicLength"/> characters.</param>
/// <param name="TopicClipped">Whether <paramref name="Topic"/> is only the start of a longer topic.</param>
/// <param name="Payload">The payload exactly as received, base64 in JSON because it need not be text.</param>
/// <param name="Reason">Why it could not become a Reading.</param>
/// <param name="ReceivedTime">When the platform received it.</param>
public sealed record RejectedMessageResponse(
    long Id, string Topic, bool TopicClipped, byte[] Payload, RejectReason Reason, DateTimeOffset ReceivedTime);
