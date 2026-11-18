using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;

namespace FieldTrack.Api.Auth;

/// <summary>Reads the signed-in user from the access token's claims.</summary>
internal static class ClaimsPrincipalExtensions
{
    /// <summary>The signed-in user's id: the JWT's sub claim, which <see cref="TokenService"/> sets.</summary>
    public static Guid UserId(this ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(JwtRegisteredClaimNames.Sub)!);
}
