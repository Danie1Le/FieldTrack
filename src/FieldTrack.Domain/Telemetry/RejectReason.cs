namespace FieldTrack.Domain.Telemetry;

/// <summary>
/// Why a message on a telemetry topic cannot become a Reading (spec #21, Telemetry contract). Rejected messages
/// store it by name, so renaming a member needs a data migration.
/// </summary>
public enum RejectReason
{
    /// <summary>
    /// Not a UTF-8 JSON object of at most <see cref="TelemetryPayload.MaxPayloadBytes"/> bytes, a field sent twice,
    /// or a field holding a value of the wrong kind (wrong type, negative <c>boot</c> or <c>seq</c>, non-finite sensor).
    /// </summary>
    BadJson,

    /// <summary>One of <c>boot</c>, <c>seq</c>, <c>ts</c>, <c>lat</c> or <c>lon</c> is absent or null.</summary>
    MissingField,

    /// <summary>Latitude outside -90 to 90 or longitude outside -180 to 180.</summary>
    PositionOutOfRange,

    /// <summary>Device time more than 5 minutes ahead of Received time, so a broken clock cannot write into the future.</summary>
    TooFarAhead,

    /// <summary>Device time more than 7 days before Received time.</summary>
    TooLate,

    /// <summary>The topic names a version of the telemetry contract other than <c>v1</c>.</summary>
    UnknownTopicVersion,

    /// <summary>No Device, Archived or not, has the serial number in the topic.</summary>
    UnknownSerialNumber,

    /// <summary>The Device with the serial number in the topic is Archived, so it may no longer write Readings.</summary>
    ArchivedDevice,
}
