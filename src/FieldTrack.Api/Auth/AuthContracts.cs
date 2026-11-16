using System.ComponentModel.DataAnnotations;

// MVC logs action arguments at Trace through ToString, so these contracts leave their secrets out
// of it (#23: no passwords or tokens in logs).

namespace FieldTrack.Api.Auth;

/// <summary>A sign-in with email and password.</summary>
/// <param name="Email">The user's email address.</param>
/// <param name="Password">The user's password.</param>
public sealed record SignInRequest([Required] string Email, [Required] string Password)
{
    /// <inheritdoc />
    public override string ToString() => $"{nameof(SignInRequest)} {{ {nameof(Email)} = {Email} }}";
}

/// <summary>A request to spend a refresh token for new tokens.</summary>
/// <param name="RefreshToken">The refresh token from the last sign-in or refresh.</param>
public sealed record RefreshRequest([Required] string RefreshToken)
{
    /// <inheritdoc />
    public override string ToString() => nameof(RefreshRequest);
}

/// <summary>
/// Tokens for a signed-in user. The dashboard keeps the access token in memory and the refresh
/// token in sessionStorage (#23); both travel in the body, not cookies (ADR 0002).
/// </summary>
/// <param name="AccessToken">The JWT to send as a bearer token.</param>
/// <param name="AccessTokenExpiresAt">When the access token expires; refresh before then.</param>
/// <param name="RefreshToken">Single-use token that buys the next pair of tokens.</param>
public sealed record TokenResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken)
{
    /// <inheritdoc />
    public override string ToString() => $"{nameof(TokenResponse)} {{ {nameof(AccessTokenExpiresAt)} = {AccessTokenExpiresAt:O} }}";
}
