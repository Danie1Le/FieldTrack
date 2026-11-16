using System.ComponentModel.DataAnnotations;

namespace FieldTrack.Api.Auth;

/// <summary>A new user, created by an Admin.</summary>
/// <param name="Email">The user's email address, which they sign in with.</param>
/// <param name="Role"><c>Admin</c> or <c>Operator</c>.</param>
/// <param name="TemporaryPassword">The password the user must replace at first sign-in.</param>
public sealed record CreateUserRequest(
    [Required, EmailAddress] string Email,
    [Required, AllowedValues(Roles.Admin, Roles.Operator)] string Role,
    [Required] string TemporaryPassword)
{
    /// <inheritdoc />
    /// <remarks>Leaves the password out: MVC logs action arguments at Trace through ToString.</remarks>
    public override string ToString() => $"{nameof(CreateUserRequest)} {{ {nameof(Email)} = {Email}, {nameof(Role)} = {Role} }}";
}

/// <summary>A user as Admins see them.</summary>
/// <param name="Id">The user's id.</param>
/// <param name="Email">The user's email address.</param>
/// <param name="Role"><c>Admin</c> or <c>Operator</c>.</param>
/// <param name="MustChangePassword">True until the user replaces their temporary password.</param>
public sealed record UserResponse(Guid Id, string Email, string Role, bool MustChangePassword);
