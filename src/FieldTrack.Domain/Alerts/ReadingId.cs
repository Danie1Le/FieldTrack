namespace FieldTrack.Domain.Alerts;

/// <summary>Identifies a stored Reading: its Device, Boot and Sequence number (ADR 0001).</summary>
/// <param name="DeviceId">The Device that sent it.</param>
/// <param name="Boot">The Device's power-on period it was taken in.</param>
/// <param name="SequenceNumber">Its position within the Boot.</param>
public sealed record ReadingId(Guid DeviceId, int Boot, long SequenceNumber)
{
    /// <summary>
    /// Whether this Reading counts as newer than <paramref name="last"/> for rule evaluation (ADR 0005): a later
    /// (Boot, Sequence number) from the same Device, or any Reading from another Device. Device time is not used
    /// because a Device clock can jump.
    /// </summary>
    public bool ComesAfter(ReadingId? last) =>
        last is null
        || DeviceId != last.DeviceId
        || Boot > last.Boot
        || (Boot == last.Boot && SequenceNumber > last.SequenceNumber);
}
