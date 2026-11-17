namespace FieldTrack.Infrastructure.Persistence;

/// <summary>
/// What a Geofence is for (spec #21 story 27). The map draws Inclusion solid and Exclusion dashed; an Alert Rule's kind,
/// enter or exit, decides what raises.
/// </summary>
public enum GeofenceKind
{
    /// <summary>An area Assets are meant to stay inside, such as a depot or work site.</summary>
    Inclusion,

    /// <summary>An area Assets are meant to stay out of, such as a restricted zone.</summary>
    Exclusion,
}
