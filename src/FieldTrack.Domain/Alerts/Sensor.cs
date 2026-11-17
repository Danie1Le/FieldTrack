namespace FieldTrack.Domain.Alerts;

/// <summary>A sensor value a Reading may carry, named as in the telemetry contract (spec #21).</summary>
public enum Sensor
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
