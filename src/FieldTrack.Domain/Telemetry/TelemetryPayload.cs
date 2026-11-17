using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Unicode;

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
    /// How far behind its Received time a Reading's Device time may be. ADR 0003 relies on this to prune
    /// Reading keys after 8 days: a later Reading could no longer be de-duplicated.
    /// </summary>
    public static readonly TimeSpan MaxLate = TimeSpan.FromDays(7);

    /// <summary>
    /// The largest payload accepted. A full Reading is about 250 bytes; the bound stops a publisher making ingest
    /// parse, hash or store megabytes. Mosquitto's <c>message_size_limit</c> matches it.
    /// </summary>
    public const int MaxPayloadBytes = 4096;

    // Case-sensitive camelCase, numbers only as JSON numbers, and each field once: anything else is not the
    // contract, and a repeated field would let ingest and a person reading the raw payload disagree.
    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowDuplicateProperties = false,
    };

    /// <summary>
    /// Reads <paramref name="payload"/> as a Reading received at <paramref name="receivedAt"/>, or says why it is refused.
    /// </summary>
    /// <param name="payload">The raw MQTT payload.</param>
    /// <param name="receivedAt">When the server received the payload.</param>
    /// <param name="reading">The Reading when the method returns <see langword="true"/>; otherwise null.</param>
    /// <param name="reason">Why the payload is refused. Meaningful only when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the payload is a Reading.</returns>
    public static bool TryParse(
        ReadOnlySpan<byte> payload,
        DateTimeOffset receivedAt,
        [NotNullWhen(true)] out Reading? reading,
        out RejectReason reason)
    {
        reading = null;

        // The JSON reader does not validate the bytes of strings it skips, so the whole payload is checked here.
        if (payload.Length > MaxPayloadBytes || !Utf8.IsValid(payload))
        {
            reason = RejectReason.BadJson;
            return false;
        }

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

        // A Boot counts up from 0 and a Sequence number restarts at 0 each Boot (ADR 0001), so a negative is not a
        // value a Device sends. A sensor past float's range parses to infinity, which a Reading cannot carry.
        if (boot < 0 || seq < 0 || !AreFinite(contract.SpeedKph, contract.EngineTempC, contract.FuelPct, contract.VibrationMmS, contract.BatteryV))
        {
            reason = RejectReason.BadJson;
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

    private static bool AreFinite(params ReadOnlySpan<float?> sensors)
    {
        foreach (var sensor in sensors)
        {
            if (sensor is { } value && !float.IsFinite(value))
            {
                return false;
            }
        }

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
