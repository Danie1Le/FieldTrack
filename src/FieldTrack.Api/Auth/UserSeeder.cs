using FieldTrack.Infrastructure.Identity;
using FieldTrack.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

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

    /// <summary>The display name of the first Admin, who has no other name to show.</summary>
    public const string AdminDisplayName = "Admin";

    /// <summary>The display name of the Demo Operator, set again on every start in demo mode.</summary>
    public const string DemoOperatorDisplayName = "Demo Operator";

    /// <summary>
    /// Ensures both roles exist, creates an Admin from configuration when none exists (it must change
    /// the configured password at first sign-in, like any user with a temporary password), and in demo
    /// mode creates the Demo Operator or resets it to the configured password.
    /// </summary>
    public static async Task SeedAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var roles = provider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var users = provider.GetRequiredService<UserManager<FieldTrackUser>>();
        var configuration = provider.GetRequiredService<IConfiguration>();
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(UserSeeder));
        var demoOperator = provider.GetRequiredService<IOptions<DemoOperatorOptions>>().Value;

        // One transaction, so a failed step never leaves a user without a role or the Demo Operator
        // without a password. Identity's stores share this scope's DbContext.
        await using var transaction = await provider.GetRequiredService<FieldTrackDbContext>().Database.BeginTransactionAsync();

        foreach (var role in Roles.All)
        {
            if (!await roles.RoleExistsAsync(role))
            {
                EnsureSucceeded(await roles.CreateAsync(new IdentityRole<Guid>(role)));
            }
        }

        await SeedAdminAsync(users, configuration, logger);
        await ResetDemoOperatorAsync(users, demoOperator);
        await transaction.CommitAsync();
    }

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

        // The configured password sits in .env or deployment settings, so it is only a temporary
        // password: the Admin replaces it at first sign-in (#61).
        var admin = new FieldTrackUser { UserName = email, Email = email, DisplayName = AdminDisplayName, MustChangePassword = true };
        EnsureSucceeded(await users.CreateAsync(admin, password));
        EnsureSucceeded(await users.AddToRoleAsync(admin, Roles.Admin));
    }

    private static async Task ResetDemoOperatorAsync(UserManager<FieldTrackUser> users, DemoOperatorOptions demoOperator)
    {
        if (!demoOperator.Enabled)
        {
            return;
        }

        var email = demoOperator.Email!;
        var password = demoOperator.Password!;
        var demo = await users.FindByEmailAsync(email);
        if (demo is null)
        {
            demo = new FieldTrackUser { UserName = email, Email = email, DisplayName = DemoOperatorDisplayName };
            EnsureSucceeded(await users.CreateAsync(demo, password));
            EnsureSucceeded(await users.AddToRoleAsync(demo, Roles.Operator));
        }
        else
        {
            // Never reset an Admin's password because the demo settings name their email.
            if (!await users.IsInRoleAsync(demo, Roles.Operator))
            {
                throw new InvalidOperationException($"{DemoOperatorOptions.EmailKey} names {email}, who is not an Operator.");
            }

            EnsureSucceeded(await users.RemovePasswordAsync(demo));
            EnsureSucceeded(await users.AddPasswordAsync(demo, password));
        }

        // The password is public and fixed by the deploy, so the Demo Operator never changes it,
        // and a lockout would only let visitors lock each other out. SetLockoutEnabledAsync saves
        // the user, these fields included.
        demo.MustChangePassword = false;
        demo.DisplayName = DemoOperatorDisplayName;
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
