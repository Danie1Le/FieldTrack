using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace FieldTrack.Domain.Telemetry;

/// <summary>
/// The telemetry contract (spec #21): camelCase UTF-8 JSON with required <c>boot</c>, <c>seq</c>, <c>ts</c>
/// (Unix epoch milliseconds), <c>lat</c> and <c>lon</c>, optional sensors, and unknown fields ignored.
/// </summary>
public static class TelemetryPayload
{
    /// <summary>How far ahead of its Received time a Reading's Device time may be.</summary>
    public static readonly TimeSpan MaxAhead = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How far behind its Received time a Reading's Device time may be. Partitions exist this far back
    /// (ADR 0003), so a later Reading would have nowhere to go.
    /// </summary>
    public static readonly TimeSpan MaxLate = TimeSpan.FromDays(7);

    // Case-sensitive camelCase, and numbers only as JSON numbers: anything else is not the contract.
    private static readonly JsonSerializerOptions _options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// Reads <paramref name="payload"/> as a Reading received at <paramref name="receivedAt"/>, or says why it is refused.
    /// </summary>
    public static bool TryParse(
        ReadOnlySpan<byte> payload,
        DateTimeOffset receivedAt,
        [NotNullWhen(true)] out Reading? reading,
        out RejectReason reason)
    {
        reading = null;
        Contract? contract;
        try
        {
            contract = JsonSerializer.Deserialize<Contract>(payload, _options);
        }
        catch (JsonException)
        {
            contract = null;
        }

        if (contract is null)
        {
            reason = RejectReason.BadJson;
            return false;
        }

        if (contract is not { Boot: { } boot, Seq: { } seq, Ts: { } ts, Lat: { } lat, Lon: { } lon })
        {
            reason = RejectReason.MissingField;
            return false;
        }

        if (lat is < -90 or > 90 || lon is < -180 or > 180)
        {
            reason = RejectReason.PositionOutOfRange;
            return false;
        }

        // Compared in milliseconds before converting, so a ts beyond DateTimeOffset's range is refused, not thrown.
        var receivedMs = receivedAt.ToUnixTimeMilliseconds();
        if (ts > receivedMs + (long)MaxAhead.TotalMilliseconds)
        {
            reason = RejectReason.TooFarAhead;
            return false;
        }

        if (ts < receivedMs - (long)MaxLate.TotalMilliseconds)
        {
            reason = RejectReason.TooLate;
            return false;
        }

        reason = default;
        reading = new Reading(
            boot, seq, DateTimeOffset.FromUnixTimeMilliseconds(ts), lat, lon,
            contract.SpeedKph, contract.EngineTempC, contract.FuelPct, contract.VibrationMmS, contract.BatteryV);
        return true;
    }

    private sealed record Contract(
        int? Boot,
        long? Seq,
        long? Ts,
        double? Lat,
        double? Lon,
        float? SpeedKph,
        float? EngineTempC,
        float? FuelPct,
        float? VibrationMmS,
        float? BatteryV);
}
