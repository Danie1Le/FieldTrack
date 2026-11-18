using System.Buffers.Text;
using System.Linq.Expressions;
using System.Security.Cryptography;
using FieldTrack.Infrastructure.Identity;
using FieldTrack.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace FieldTrack.Api.Auth;

/// <summary>
/// Issues access tokens and refresh tokens, rotates refresh tokens and revokes them (ADR 0002).
/// </summary>
public sealed class TokenService(
    FieldTrackDbContext db,
    UserManager<FieldTrackUser> users,
    IOptions<AuthOptions> options,
    TimeProvider time)
{
    /// <summary>The issuer and audience of every access token: the API signs tokens only for itself.</summary>
    public const string Issuer = "fieldtrack";

    /// <summary>The access token claim that carries the user's roles; JWT validation reads roles from it.</summary>
    public const string RoleClaim = "role";

    /// <summary>How long an access token is valid (#23).</summary>
    public static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(15);

    /// <summary>How long a refresh token is valid. Each refresh issues a fresh one, so a user who keeps refreshing stays signed in.</summary>
    public static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(7);

    /// <summary>Issues a new access token and a refresh token that starts a new family for <paramref name="user"/>.</summary>
    public Task<TokenResponse> IssueAsync(FieldTrackUser user, CancellationToken cancellationToken) =>
        IssueAsync(user, Guid.NewGuid(), cancellationToken);

    private async Task<TokenResponse> IssueAsync(FieldTrackUser user, Guid familyId, CancellationToken cancellationToken)
    {
        // JWT times are whole seconds; truncate so the reported expiry equals the token's exp claim.
        var now = DateTimeOffset.FromUnixTimeSeconds(time.GetUtcNow().ToUnixTimeSeconds());
        var expiresAt = now + AccessTokenLifetime;
        var accessToken = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Issuer,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = user.Id.ToString(),
                [JwtRegisteredClaimNames.Email] = user.Email!,
                [RoleClaim] = (await users.GetRolesAsync(user)).ToArray(),
            },
            SigningCredentials = new SigningCredentials(options.Value.CreateSigningKey(), SecurityAlgorithms.HmacSha256),
        });

        var refreshToken = RandomNumberGenerator.GetBytes(32);
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            FamilyId = familyId,
            TokenHash = SHA256.HashData(refreshToken),
            CreatedAt = now,
            ExpiresAt = now + RefreshTokenLifetime,
        });
        await db.SaveChangesAsync(cancellationToken);

        return new TokenResponse(accessToken, expiresAt, Base64Url.EncodeToString(refreshToken));
    }

    /// <summary>
    /// Spends <paramref name="refreshToken"/> and issues new tokens, or returns null when the token
    /// is unknown, expired or already spent.
    /// </summary>
    public async Task<TokenResponse?> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        if (!Base64Url.IsValid(refreshToken))
        {
            return null;
        }

        var hash = SHA256.HashData(Base64Url.DecodeFromChars(refreshToken));
        var now = time.GetUtcNow();
        // Spend and reissue in one transaction: a replay racing with this refresh waits on the row
        // lock until the new token is committed, so its revoke reaches that token too.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // A conditional update, so two refreshes racing with one token cannot both succeed.
        var spent = await db.RefreshTokens
            .Where(token => token.TokenHash == hash && token.RevokedAt == null && token.ExpiresAt > now)
            .ExecuteUpdateAsync(token => token.SetProperty(t => t.RevokedAt, now), cancellationToken);
        var stored = await db.RefreshTokens.AsNoTracking().SingleOrDefaultAsync(token => token.TokenHash == hash, cancellationToken);
        if (stored is null)
        {
            return null;
        }

        if (spent == 0)
        {
            if (stored.RevokedAt is not null)
            {
                // A spent token presented again means it was copied: revoke every refresh token of that user,
                // or only this sign-in's family for the Demo Operator, whom every visitor shares (ADR 0002).
                if (await db.Users.AnyAsync(user => user.Id == stored.UserId && user.IsDemoOperator, cancellationToken))
                {
                    // A family id is unique per user only: tokens of other users can share it.
                    await RevokeAsync(token => token.UserId == stored.UserId && token.FamilyId == stored.FamilyId, cancellationToken);
                }
                else
                {
                    await RevokeAllAsync(stored.UserId, cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
            }

            return null;
        }

        var user = await users.FindByIdAsync(stored.UserId.ToString());
        // A disabled Demo Operator refreshes nothing, not even a token committed after startup revoked the
        // others (#135). Other users' lockouts come from failed sign-ins, which anyone who knows the email
        // can cause, so those block new sign-ins only and leave sessions alone.
        if (user is null || (user.IsDemoOperator && await users.IsLockedOutAsync(user)))
        {
            return null;
        }

        var response = await IssueAsync(user, stored.FamilyId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return response;
    }

    /// <summary>Revokes every refresh token of the user, so they must sign in again within one access token lifetime.</summary>
    public Task RevokeAllAsync(Guid userId, CancellationToken cancellationToken) =>
        RevokeAsync(token => token.UserId == userId, cancellationToken);

    private async Task RevokeAsync(Expression<Func<RefreshToken, bool>> which, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await db.RefreshTokens
            .Where(which)
            .Where(token => token.RevokedAt == null)
            .ExecuteUpdateAsync(token => token.SetProperty(t => t.RevokedAt, now), cancellationToken);
    }
}
