using FieldTrack.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FieldTrack.Api.Auth;

/// <summary>Sign-in and token refresh: the only endpoints a signed-out client may call besides health and docs.</summary>
[ApiController]
[Route("auth")]
[AllowAnonymous]
public sealed class AuthController(
    UserManager<FieldTrackUser> users,
    SignInManager<FieldTrackUser> signInManager,
    TokenService tokens) : ControllerBase
{
    /// <summary>Signs in with email and password.</summary>
    /// <response code="401">Wrong email or password, or the account is locked out after repeated failures.</response>
    [HttpPost("sign-in")]
    [EnableRateLimiting(AuthServiceCollectionExtensions.SignInRateLimit)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<TokenResponse>> SignIn(SignInRequest request, CancellationToken cancellationToken)
    {
        var user = await users.FindByEmailAsync(request.Email);
        // One answer for an unknown email, a wrong password and a lockout, so none reveals which accounts exist.
        if (user is null || !(await signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true)).Succeeded)
        {
            return Unauthorized();
        }

        return await tokens.IssueAsync(user, cancellationToken);
    }

    /// <summary>Spends a refresh token for a new access token and refresh token.</summary>
    /// <response code="401">The refresh token is unknown, expired, revoked or already spent.</response>
    [HttpPost("refresh")]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<TokenResponse>> Refresh(RefreshRequest request, CancellationToken cancellationToken) =>
        await tokens.RefreshAsync(request.RefreshToken, cancellationToken) is { } response ? response : Unauthorized();
}
