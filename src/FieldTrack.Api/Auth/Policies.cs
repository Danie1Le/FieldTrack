namespace FieldTrack.Api.Auth;

/// <summary>
/// Authorization policy names for endpoints. Roles limit what a user may change, not what they may see
/// (spec #21, Auth), with two exceptions read under <see cref="Manage"/>: the user list, which exposes accounts,
/// and Rejected messages, which hold raw input from anyone who can publish (ADR 0002).
/// </summary>
public static class Policies
{
    /// <summary>Any signed-in user: every read. The fallback policy for endpoints that name none requires the same.</summary>
    public const string Read = "Read";

    /// <summary>Admins only: changes to Assets, Devices, Geofences, Alert Rules and users, and reading users and Rejected messages.</summary>
    public const string Manage = "Manage";

    /// <summary>Admins and Operators: Acknowledge and Resolve Alerts.</summary>
    public const string AlertActions = "AlertActions";
}
