using Microsoft.AspNetCore.Identity;

namespace FieldTrack.Infrastructure.Identity;

/// <summary>
/// A person who signs in to FieldTrack, as an Admin or an Operator (ADR 0002). ASP.NET Core
/// Identity stores it with its password hash, roles and lockout state. Keyed by a Guid, like every
/// other FieldTrack row, so foreign keys to users are <c>uuid</c>.
/// </summary>
public sealed class FieldTrackUser : IdentityUser<Guid>
{
    /// <summary>The longest <see cref="DisplayName"/>.</summary>
    public const int MaxDisplayNameLength = 100;

    /// <summary>
    /// The name other users see on what this user did, such as who Acknowledged or Resolved an Alert. Only Admins
    /// see emails: an email is half of a sign-in, so it is never shown to every signed-in user (#31).
    /// </summary>
    public required string DisplayName { get; set; }

    /// <summary>
    /// True while the user still has the temporary password an Admin gave them; they must choose
    /// their own before they get tokens.
    /// </summary>
    public bool MustChangePassword { get; set; }
}
