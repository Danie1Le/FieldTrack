using FieldTrack.Domain.Telemetry;

namespace FieldTrack.Infrastructure.Ingest;

/// <summary>A message ingest rejected for its topic or payload, before it is stored as a Rejected message.</summary>
/// <param name="Topic">The MQTT topic it arrived on.</param>
/// <param name="Payload">The payload exactly as received.</param>
/// <param name="Reason">Why it cannot become a Reading.</param>
/// <param name="ReceivedTime">When the platform received the message.</param>
/// <param name="Redelivered">Whether the broker marked the message as sent before (MQTT DUP).</param>
public sealed record ReceivedRejectedMessage(string Topic, byte[] Payload, RejectReason Reason, DateTimeOffset ReceivedTime, bool Redelivered);
