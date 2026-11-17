using FieldTrack.Domain.Telemetry;

namespace FieldTrack.Infrastructure.Ingest;

/// <summary>A Reading as ingest received it, before it is stored.</summary>
/// <param name="SerialNumber">The Device's serial number, from the MQTT topic.</param>
/// <param name="Reading">The parsed payload.</param>
/// <param name="ReceivedTime">When the platform received the message.</param>
/// <param name="ContentHash">SHA-256 of the raw payload, which tells identical from conflicting duplicates (ADR 0003).</param>
public sealed record ReceivedReading(string SerialNumber, Reading Reading, DateTimeOffset ReceivedTime, byte[] ContentHash);
