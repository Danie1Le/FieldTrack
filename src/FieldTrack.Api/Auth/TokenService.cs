using System.Buffers.Text;
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

    /// <summary>How long an access token is valid (ADR 0002).</summary>
    public static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(15);

    /// <summary>How long a refresh token is valid. Each refresh issues a fresh one, so an active session never expires.</summary>
    public static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(7);

    /// <summary>Issues a new access token and refresh token for <paramref name="user"/>.</summary>
    public async Task<TokenResponse> IssueAsync(FieldTrackUser user, CancellationToken cancellationToken)
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
                [JwtRegisteredClaimNames.Sub] = user.Id,
                [JwtRegisteredClaimNames.Email] = user.Email!,
                ["role"] = (await users.GetRolesAsync(user)).ToArray(),
            },
            SigningCredentials = new SigningCredentials(options.Value.CreateSigningKey(), SecurityAlgorithms.HmacSha256),
        });

        var refreshToken = RandomNumberGenerator.GetBytes(32);
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
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
                // A spent token presented again means it was copied: end every session of that user.
                await RevokeAllAsync(stored.UserId, cancellationToken);
            }

            return null;
        }

        var user = await users.FindByIdAsync(stored.UserId);
        return user is null ? null : await IssueAsync(user, cancellationToken);
    }

    /// <summary>Revokes every refresh token of the user, so they must sign in again within one access token lifetime.</summary>
    public async Task RevokeAllAsync(string userId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await db.RefreshTokens
            .Where(token => token.UserId == userId && token.RevokedAt == null)
            .ExecuteUpdateAsync(token => token.SetProperty(t => t.RevokedAt, now), cancellationToken);
    }
}
