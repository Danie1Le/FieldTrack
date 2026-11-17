using FieldTrack.Infrastructure.Identity;
using FieldTrack.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FieldTrack.Api.Auth;

/// <summary>
/// User management, for Admins only. Users never register themselves: an Admin creates each one
/// with a temporary password (spec #21, Auth).
/// </summary>
[ApiController]
[Route("users")]
[Authorize(Policy = Policies.Manage)]
public sealed class UsersController(FieldTrackDbContext db, UserManager<FieldTrackUser> users, TokenService tokens) : ControllerBase
{
    /// <summary>Lists every user with their role.</summary>
    [HttpGet]
    public async Task<IReadOnlyList<UserResponse>> List(CancellationToken cancellationToken) =>
        await (
            from user in db.Users
            join userRole in db.UserRoles on user.Id equals userRole.UserId
            join role in db.Roles on userRole.RoleId equals role.Id
            orderby user.Email
            select new UserResponse(user.Id, user.Email!, user.DisplayName, role.Name!, user.MustChangePassword))
        .ToListAsync(cancellationToken);

    /// <summary>Creates an Admin or Operator who must change the temporary password at first sign-in.</summary>
    /// <response code="400">Unknown role, weak password, or the email is already in use.</response>
    [HttpPost]
    [ProducesResponseType<UserResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<UserResponse>> Create(CreateUserRequest request, CancellationToken cancellationToken)
    {
        var user = new FieldTrackUser { UserName = request.Email, Email = request.Email, DisplayName = request.DisplayName, MustChangePassword = true };
        // One transaction, so a user is never left without a role.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var result = await users.CreateAsync(user, request.TemporaryPassword);
        if (result.Succeeded)
        {
            result = await users.AddToRoleAsync(user, request.Role);
        }

        if (!result.Succeeded)
        {
            // The username is the email: Identity reports a taken one twice; the Admin sees no username, so keep one message.
            var errors = result.Errors.Any(error => error.Code == nameof(IdentityErrorDescriber.DuplicateEmail))
                ? result.Errors.Where(error => error.Code != nameof(IdentityErrorDescriber.DuplicateUserName))
                : result.Errors;
            return this.IdentityValidationProblem(IdentityResult.Failed(errors.ToArray()));
        }

        await transaction.CommitAsync(cancellationToken);
        return StatusCode(StatusCodes.Status201Created, new UserResponse(user.Id, user.Email, user.DisplayName, request.Role, user.MustChangePassword));
    }

    /// <summary>
    /// Revokes all the user's refresh tokens, cutting them off once their access token expires, at
    /// most 15 minutes later (ADR 0002).
    /// </summary>
    /// <response code="404">No user has this id.</response>
    [HttpDelete("{id:guid}/refresh-tokens")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeRefreshTokens(Guid id, CancellationToken cancellationToken)
    {
        if (await users.FindByIdAsync(id.ToString()) is null)
        {
            return NotFound();
        }

        await tokens.RevokeAllAsync(id, cancellationToken);
        return NoContent();
    }
}
