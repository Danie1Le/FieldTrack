namespace FieldTrack.Api.Assets;

/// <summary>A stored Reading as anyone signed in sees it in an Asset's history.</summary>
/// <param name="DeviceSerialNumber">The Device that sent it; an Asset's history can span several Devices.</param>
/// <param name="Boot">The Device's Boot it was taken in.</param>
/// <param name="SequenceNumber">Its Sequence number within that Boot.</param>
/// <param name="DeviceTime">When the Device took it, by its own clock.</param>
/// <param name="ReceivedTime">When the platform received it.</param>
/// <param name="Latitude">WGS 84 latitude in degrees.</param>
/// <param name="Longitude">WGS 84 longitude in degrees.</param>
/// <param name="SpeedKph">Speed in km/h, or null when the Device has no such sensor.</param>
/// <param name="EngineTempC">Engine temperature in °C, or null.</param>
/// <param name="FuelPct">Fuel level in percent, or null.</param>
/// <param name="VibrationMmS">Vibration velocity in mm/s, or null.</param>
/// <param name="BatteryV">Battery voltage in volts, or null.</param>
public sealed record ReadingResponse(
    string DeviceSerialNumber,
    int Boot,
    long SequenceNumber,
    DateTimeOffset DeviceTime,
    DateTimeOffset ReceivedTime,
    double Latitude,
    double Longitude,
    float? SpeedKph,
    float? EngineTempC,
    float? FuelPct,
    float? VibrationMmS,
    float? BatteryV);
