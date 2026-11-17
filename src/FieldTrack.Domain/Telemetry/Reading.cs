namespace FieldTrack.Domain.Telemetry;

/// <summary>
/// One report from a Device at one instant, as the telemetry contract carries it (spec #21). The Device
/// itself comes from the MQTT topic, so it is not part of the payload; together with <see cref="Boot"/> and
/// <see cref="SequenceNumber"/> it identifies the Reading (ADR 0001).
/// </summary>
/// <param name="Boot">The Device's power-on period the Reading was taken in.</param>
/// <param name="SequenceNumber">The Reading's position within its Boot.</param>
/// <param name="DeviceTime">When the Device says it took the Reading, to the millisecond.</param>
/// <param name="Latitude">WGS 84 latitude in degrees, -90 to 90.</param>
/// <param name="Longitude">WGS 84 longitude in degrees, -180 to 180.</param>
/// <param name="SpeedKph">Speed in km/h, or null when the Device has no such sensor.</param>
/// <param name="EngineTempC">Engine temperature in °C, or null.</param>
/// <param name="FuelPct">Fuel level in percent, or null.</param>
/// <param name="VibrationMmS">Vibration velocity in mm/s, or null.</param>
/// <param name="BatteryV">Battery voltage in volts, or null.</param>
public sealed record Reading(
    int Boot,
    long SequenceNumber,
    DateTimeOffset DeviceTime,
    double Latitude,
    double Longitude,
    float? SpeedKph,
    float? EngineTempC,
    float? FuelPct,
    float? VibrationMmS,
    float? BatteryV);
