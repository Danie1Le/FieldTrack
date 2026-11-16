namespace FieldTrack.Api.Auth;

/// <summary>
/// Authorization policy names for endpoints. Roles limit what a user may change, never what
/// they may see (spec #21, Auth).
/// </summary>
public static class Policies
{
    /// <summary>Any signed-in user: every read. Also the fallback for endpoints that name no policy.</summary>
    public const string Read = "Read";

    /// <summary>Admins only: changes to Assets, Devices, Geofences, Alert Rules and users.</summary>
    public const string Manage = "Manage";

    /// <summary>Admins and Operators: Acknowledge and Resolve Alerts.</summary>
    public const string AlertActions = "AlertActions";
}
