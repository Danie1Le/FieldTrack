namespace FieldTrack.Domain.Telemetry;

/// <summary>Why a telemetry payload cannot become a Reading (spec #21, Telemetry contract).</summary>
public enum RejectReason
{
    /// <summary>Not a UTF-8 JSON object, or a field holds the wrong type of value.</summary>
    BadJson,

    /// <summary>One of <c>boot</c>, <c>seq</c>, <c>ts</c>, <c>lat</c> or <c>lon</c> is absent or null.</summary>
    MissingField,

    /// <summary>Latitude outside -90 to 90 or longitude outside -180 to 180.</summary>
    PositionOutOfRange,

    /// <summary>Device time more than 5 minutes ahead of Received time, so a broken clock cannot write into the future.</summary>
    TooFarAhead,

    /// <summary>Device time more than 7 days before Received time.</summary>
    TooLate,
}
