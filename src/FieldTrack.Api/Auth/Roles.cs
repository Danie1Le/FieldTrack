namespace FieldTrack.Api.Auth;

/// <summary>The roles a user can hold. Every user holds exactly one.</summary>
public static class Roles
{
    /// <summary>Manages Assets, Devices, Geofences, Alert Rules and users.</summary>
    public const string Admin = "Admin";

    /// <summary>Watches the map and acts on Alerts; changes nothing else.</summary>
    public const string Operator = "Operator";

    /// <summary>Every role, for seeding.</summary>
    public static readonly IReadOnlyList<string> All = [Admin, Operator];
}
