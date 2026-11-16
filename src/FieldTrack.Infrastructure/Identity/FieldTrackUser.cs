using Microsoft.AspNetCore.Identity;

namespace FieldTrack.Infrastructure.Identity;

/// <summary>
/// A person who signs in to FieldTrack, as an Admin or an Operator (ADR 0002). ASP.NET Core
/// Identity stores it with its password hash, roles and lockout state.
/// </summary>
public sealed class FieldTrackUser : IdentityUser
{
    /// <summary>
    /// True while the user still has the temporary password an Admin gave them; they must choose
    /// their own before they get tokens.
    /// </summary>
    public bool MustChangePassword { get; set; }
}
