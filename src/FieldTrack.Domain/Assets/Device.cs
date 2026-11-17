namespace FieldTrack.Domain.Assets;

/// <summary>
/// The transmitter that reports Readings. Telemetry names it by <see cref="SerialNumber"/> in its
/// MQTT topic; it is fitted to at most one Asset at a time through an <see cref="Attachment"/>.
/// </summary>
public sealed class Device
{
    /// <summary>Surrogate key.</summary>
    public Guid Id { get; set; }

    /// <summary>The serial number the Device publishes under; unique across all Devices, Archived ones included.</summary>
    public required string SerialNumber { get; set; }

    /// <summary>
    /// True once the Device is removed from use but kept because it has history. Archived Devices
    /// are left out of lists unless asked for, and their telemetry is rejected (spec #21).
    /// </summary>
    public bool Archived { get; set; }

    /// <summary>
    /// The Received time of the Device's latest stored batch, or null before its first. Ingest sets it
    /// so offline detection and Asset Status survive a restart (ADR 0005).
    /// </summary>
    public DateTimeOffset? LastReceivedAt { get; set; }
}
