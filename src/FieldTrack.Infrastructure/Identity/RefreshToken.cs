namespace FieldTrack.Infrastructure.Identity;

/// <summary>
/// One refresh token issued to a user. Only its SHA-256 hash is stored, so a database leak does
/// not let anyone refresh as the user. Each refresh revokes the token it used and issues a new one (ADR 0002).
/// </summary>
public sealed class RefreshToken
{
    /// <summary>Surrogate key.</summary>
    public Guid Id { get; set; }

    /// <summary>The user the token signs in.</summary>
    public Guid UserId { get; set; }

    /// <summary>SHA-256 of the token the client holds.</summary>
    public required byte[] TokenHash { get; set; }

    /// <summary>When the token was issued.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>After this the token no longer refreshes.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>When the token was used, or revoked by an Admin or a password change; null while it is usable.</summary>
    public DateTimeOffset? RevokedAt { get; set; }
}
