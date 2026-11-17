namespace FieldTrack.Simulator;

/// <summary>
/// A sensor a simulated Device can carry. Each member is one optional field of the telemetry contract,
/// named as the contract names it.
/// </summary>
internal enum Sensor
{
    /// <summary>Speed in km/h.</summary>
    SpeedKph,

    /// <summary>Engine temperature in °C.</summary>
    EngineTempC,

    /// <summary>Fuel level in percent.</summary>
    FuelPct,

    /// <summary>Vibration velocity in mm/s.</summary>
    VibrationMmS,

    /// <summary>Battery voltage in volts.</summary>
    BatteryV,
}
