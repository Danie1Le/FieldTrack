using FieldTrack.Domain.Telemetry;

namespace FieldTrack.Infrastructure.Ingest;

/// <summary>A Reading as ingest received it, before it is stored or, from an unknown or Archived Device, rejected.</summary>
/// <param name="Topic">The MQTT topic it arrived on, kept if the Reading becomes a Rejected message.</param>
/// <param name="SerialNumber">The Device's serial number, from the MQTT topic.</param>
/// <param name="Reading">The parsed payload.</param>
/// <param name="ReceivedTime">When the platform received the message.</param>
/// <param name="Payload">The raw payload: hashed to tell identical from conflicting duplicates (ADR 0003), and kept if rejected.</param>
public sealed record ReceivedReading(string Topic, string SerialNumber, Reading Reading, DateTimeOffset ReceivedTime, byte[] Payload);
