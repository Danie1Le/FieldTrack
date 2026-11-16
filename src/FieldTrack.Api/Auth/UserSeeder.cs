using FieldTrack.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace FieldTrack.Api.Auth;

/// <summary>
/// Creates the roles, the first Admin and, in demo mode, the Demo Operator at startup, from
/// environment settings, so no default password lives in code (spec #21, Auth).
/// </summary>
public static partial class UserSeeder
{
    /// <summary>Configuration key for the email of the Admin created when no Admin exists.</summary>
    public const string AdminEmailKey = "FIELDTRACK_ADMIN_EMAIL";

    /// <summary>Configuration key for the password of the Admin created when no Admin exists.</summary>
    public const string AdminPasswordKey = "FIELDTRACK_ADMIN_PASSWORD";

    /// <summary>Email of the Demo Operator; required in demo mode.</summary>
    public const string DemoEmailKey = "FIELDTRACK_DEMO_EMAIL";

    /// <summary>The Demo Operator's password, shown on the login page. Setting it turns demo mode on.</summary>
    public const string DemoPasswordKey = "FIELDTRACK_DEMO_PASSWORD";

    /// <summary>
    /// Ensures both roles exist, creates an Admin from configuration when none exists, and in demo
    /// mode creates the Demo Operator or resets it to the configured password.
    /// </summary>
    public static async Task SeedAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var roles = provider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var users = provider.GetRequiredService<UserManager<FieldTrackUser>>();
        var configuration = provider.GetRequiredService<IConfiguration>();

        foreach (var role in Roles.All)
        {
            if (!await roles.RoleExistsAsync(role))
            {
                EnsureSucceeded(await roles.CreateAsync(new IdentityRole<Guid>(role)));
            }
        }

        await SeedAdminAsync(users, configuration, provider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(UserSeeder)));
        await ResetDemoOperatorAsync(users, configuration);
    }

    /// <summary>True when demo mode is on and <paramref name="user"/> is the Demo Operator.</summary>
    public static bool IsDemoOperator(IConfiguration configuration, FieldTrackUser user) =>
        !string.IsNullOrEmpty(configuration[DemoPasswordKey])
        && string.Equals(user.Email, configuration[DemoEmailKey], StringComparison.OrdinalIgnoreCase);

    private static async Task SeedAdminAsync(UserManager<FieldTrackUser> users, IConfiguration configuration, ILogger logger)
    {
        if ((await users.GetUsersInRoleAsync(Roles.Admin)).Count > 0)
        {
            return;
        }

        var email = configuration[AdminEmailKey];
        var password = configuration[AdminPasswordKey];
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
        {
            LogNoAdmin(logger, AdminEmailKey, AdminPasswordKey);
            return;
        }

        // Never promote or overwrite an existing user because the Admin settings name their email.
        if (await users.FindByEmailAsync(email) is not null)
        {
            LogAdminEmailTaken(logger, AdminEmailKey);
            return;
        }

        var admin = new FieldTrackUser { UserName = email, Email = email };
        EnsureSucceeded(await users.CreateAsync(admin, password));
        EnsureSucceeded(await users.AddToRoleAsync(admin, Roles.Admin));
    }

    private static async Task ResetDemoOperatorAsync(UserManager<FieldTrackUser> users, IConfiguration configuration)
    {
        var password = configuration[DemoPasswordKey];
        if (string.IsNullOrEmpty(password))
        {
            return;
        }

        var email = configuration[DemoEmailKey];
        if (string.IsNullOrEmpty(email))
        {
            throw new InvalidOperationException($"{DemoPasswordKey} turns demo mode on, which needs {DemoEmailKey} too.");
        }

        var demo = await users.FindByEmailAsync(email);
        if (demo is null)
        {
            demo = new FieldTrackUser { UserName = email, Email = email };
            EnsureSucceeded(await users.CreateAsync(demo, password));
            EnsureSucceeded(await users.AddToRoleAsync(demo, Roles.Operator));
        }
        else
        {
            // Never reset an Admin's password because the demo settings name their email.
            if (!await users.IsInRoleAsync(demo, Roles.Operator))
            {
                throw new InvalidOperationException($"{DemoEmailKey} names {email}, who is not an Operator.");
            }

            EnsureSucceeded(await users.RemovePasswordAsync(demo));
            EnsureSucceeded(await users.AddPasswordAsync(demo, password));
        }

        // The password is public, so a lockout would only let visitors lock each other out.
        EnsureSucceeded(await users.SetLockoutEnabledAsync(demo, false));
    }

    private static void EnsureSucceeded(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Seeding users failed: {string.Join(" ", result.Errors.Select(error => error.Description))}");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No Admin exists and {EmailKey} or {PasswordKey} is not set, so nobody can manage users.")]
    private static partial void LogNoAdmin(ILogger logger, string emailKey, string passwordKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No Admin exists and the user {EmailKey} names is not an Admin, so nobody can manage users.")]
    private static partial void LogAdminEmailTaken(ILogger logger, string emailKey);
}
