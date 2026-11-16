using FieldTrack.Infrastructure.Identity;

namespace FieldTrack.Api.Auth;

/// <summary>
/// Demo mode for the cloud demo: the Demo Operator's email and its password, which the login page
/// shows. Setting the password turns demo mode on (spec #21, Auth).
/// </summary>
public sealed class DemoOperatorOptions
{
    /// <summary>Configuration key for the Demo Operator's email; required in demo mode.</summary>
    public const string EmailKey = "FIELDTRACK_DEMO_EMAIL";

    /// <summary>Configuration key for the Demo Operator's password. Setting it turns demo mode on.</summary>
    public const string PasswordKey = "FIELDTRACK_DEMO_PASSWORD";

    /// <summary>The Demo Operator's email.</summary>
    public string? Email { get; set; }

    /// <summary>The Demo Operator's password, reset on every startup.</summary>
    public string? Password { get; set; }

    /// <summary>True in demo mode.</summary>
    public bool Enabled => !string.IsNullOrEmpty(Password);

    /// <summary>True in demo mode when <paramref name="user"/> is the Demo Operator.</summary>
    public bool IsDemoOperator(FieldTrackUser user) =>
        Enabled && string.Equals(user.Email, Email, StringComparison.OrdinalIgnoreCase);
}
