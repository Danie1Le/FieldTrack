using FieldTrack.Domain.Telemetry;

namespace FieldTrack.Domain.Alerts;

/// <summary>A Reading ingest has just stored, with the Device that sent it, which completes its identity (ADR 0001).</summary>
/// <param name="DeviceId">The Device that sent it.</param>
/// <param name="Reading">The Reading itself.</param>
public sealed record StoredReading(Guid DeviceId, Reading Reading)
{
    /// <summary>The Reading's identity.</summary>
    public ReadingId Id => new(DeviceId, Reading.Boot, Reading.SequenceNumber);
}
