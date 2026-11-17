using FieldTrack.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace FieldTrack.Api.Auth;

/// Sign-in, token refresh, password change and, in demo mode only, the Demo Operator's credentials:
/// the only endpoints a signed-out client may call besides health and docs.
/// besides health and docs.
/// </summary>
[ApiController]
[Route("auth")]
[AllowAnonymous]
public sealed class AuthController(
    UserManager<FieldTrackUser> users,
    SignInManager<FieldTrackUser> signInManager,
    TokenService tokens,
    IOptions<DemoOperatorOptions> demoOperator) : ControllerBase
{
    private static readonly FieldTrackUser DummyUser = new() { DisplayName = "unused" };
    private static readonly string DummyPasswordHash = new PasswordHasher<FieldTrackUser>().HashPassword(DummyUser, "unused");

    /// <summary>Signs in with email and password.</summary>
    /// <response code="401">Wrong email or password, or the account is locked out after repeated failures.</response>
    /// <response code="403">The password is temporary: change it with <c>POST /auth/change-password</c> to get tokens.</response>
    [HttpPost("sign-in")]
    [EnableRateLimiting(AuthServiceCollectionExtensions.SignInRateLimit)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<TokenResponse>> SignIn(SignInRequest request, CancellationToken cancellationToken)
    {
        if (await CheckPasswordAsync(request.Email, request.Password) is not { } user)
        {
            return Unauthorized();
        }

        if (user.MustChangePassword)
        {
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: "Password change required",
                detail: "This password is temporary. Choose a new one with POST /auth/change-password.");
        }

        return await tokens.IssueAsync(user, cancellationToken);
    }

    /// <summary>Spends a refresh token for a new access token and refresh token.</summary>
    /// <response code="401">The refresh token is unknown, expired, revoked or already spent.</response>
    [HttpPost("refresh")]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<TokenResponse>> Refresh(RefreshRequest request, CancellationToken cancellationToken) =>
        await tokens.RefreshAsync(request.RefreshToken, cancellationToken) is { } response ? response : Unauthorized();

    /// <summary>
    /// Replaces the user's password and signs them in. This is how a first sign-in with a
    /// temporary password completes. All the user's other refresh tokens are revoked.
    /// </summary>
    /// <response code="400">The new password is too weak or the same as the current one.</response>
    /// <response code="401">Wrong email or current password, or the account is locked out.</response>
    /// <response code="403">The Demo Operator's password cannot be changed.</response>
    [HttpPost("change-password")]
    [EnableRateLimiting(AuthServiceCollectionExtensions.SignInRateLimit)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<TokenResponse>> ChangePassword(ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        if (await CheckPasswordAsync(request.Email, request.CurrentPassword) is not { } user)
        {
            return Unauthorized();
        }

        if (demoOperator.Value.IsDemoOperator(user))
        {
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: "The Demo Operator's password cannot be changed.");
        }

        // Identity accepts the same password again, which would keep a temporary password for good (#23).
        if (request.NewPassword == request.CurrentPassword)
        {
            ModelState.AddModelError(nameof(request.NewPassword), "The new password must differ from the current one.");
            return ValidationProblem(ModelState);
        }

        // ChangePasswordAsync saves the user, the cleared flag included.
        user.MustChangePassword = false;
        var result = await users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
        {
            return this.IdentityValidationProblem(result);
        }

        await tokens.RevokeAllAsync(user.Id, cancellationToken);
        return await tokens.IssueAsync(user, cancellationToken);
    }

    /// <summary>
    /// The Demo Operator's email and password, which the login page shows in demo mode so visitors
    /// can sign in without an account. The password is public by design: it is reset on every deploy
    /// and cannot be changed (ADR 0002).
    /// </summary>
    /// <response code="404">Demo mode is off.</response>
    [HttpGet("demo-operator")]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<DemoOperatorResponse> DemoOperator() =>
        demoOperator.Value is { Enabled: true } demo ? new DemoOperatorResponse(demo.Email!, demo.Password!) : NotFound();

    /// <summary>
    /// The user with this email and password, or null. Failures count towards lockout, and an
    /// unknown email, a wrong password and a lockout look the same so none reveals which accounts exist.
    /// </summary>
    private async Task<FieldTrackUser?> CheckPasswordAsync(string email, string password)
    {
        var user = await users.FindByEmailAsync(email);
        var result = user is null ? null : await signInManager.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);
        if (result is null or { IsLockedOut: true })
        {
            // Unknown emails and locked-out accounts skip the real hash check; hash anyway so the
            // response time does not tell them apart from a wrong password.
            users.PasswordHasher.VerifyHashedPassword(DummyUser, DummyPasswordHash, password);
        }

        return result is { Succeeded: true } ? user : null;
    }
}
