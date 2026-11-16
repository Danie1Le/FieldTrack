using System.ComponentModel.DataAnnotations;
using Microsoft.IdentityModel.Tokens;

namespace FieldTrack.Api.Auth;

/// <summary>Settings for issuing and checking tokens, bound from the <c>Auth</c> section.</summary>
public sealed class AuthOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Auth";

    /// <summary>
    /// Base64 of the HS256 signing secret, at least 256 bits (#23). It comes from user-secrets
    /// locally and from Key Vault when deployed, never from a committed file.
    /// </summary>
    [Required]
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>Sign-in and password-change attempts allowed per client address per minute.</summary>
    [Range(1, int.MaxValue)]
    public int SignInPermitsPerMinute { get; set; } = 10;

    /// <summary>The key that signs and validates access tokens.</summary>
    public SymmetricSecurityKey CreateSigningKey() => new(Convert.FromBase64String(SigningKey));

    /// <summary>True when <see cref="SigningKey"/> is base64 of at least 256 bits, the minimum for HS256.</summary>
    internal bool HasStrongSigningKey()
    {
        var bytes = new byte[SigningKey.Length];
        return Convert.TryFromBase64String(SigningKey, bytes, out var length) && length >= 32;
    }
}
