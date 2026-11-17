namespace FieldTrack.Domain.Alerts;

/// <summary>How urgently an Alert needs attention; set on its Alert Rule and copied to each Alert it raises.</summary>
public enum Severity
{
    /// <summary>Worth a look soon, such as low fuel or leaving a yard.</summary>
    Warning,

    /// <summary>Needs attention now, such as an overheating engine.</summary>
    Critical,
}
