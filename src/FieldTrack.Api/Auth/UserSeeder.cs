using FieldTrack.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace FieldTrack.Api.Auth;

/// <summary>
/// Creates the roles and the first Admin at startup, from environment settings, so no default
/// password lives in code (spec #21, Auth).
/// </summary>
public static partial class UserSeeder
{
    /// <summary>Email of the Admin created when no Admin exists.</summary>
    public const string AdminEmailKey = "FIELDTRACK_ADMIN_EMAIL";

    /// <summary>Password of the Admin created when no Admin exists.</summary>
    public const string AdminPasswordKey = "FIELDTRACK_ADMIN_PASSWORD";

    /// <summary>Ensures both roles exist and, when no Admin exists, creates one from configuration.</summary>
    public static async Task SeedAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var roles = provider.GetRequiredService<RoleManager<IdentityRole>>();
        var users = provider.GetRequiredService<UserManager<FieldTrackUser>>();

        foreach (var role in Roles.All)
        {
            if (!await roles.RoleExistsAsync(role))
            {
                EnsureSucceeded(await roles.CreateAsync(new IdentityRole(role)));
            }
        }

        if ((await users.GetUsersInRoleAsync(Roles.Admin)).Count > 0)
        {
            return;
        }

        var configuration = provider.GetRequiredService<IConfiguration>();
        var email = configuration[AdminEmailKey];
        var password = configuration[AdminPasswordKey];
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
        {
            LogNoAdmin(provider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(UserSeeder)), AdminEmailKey, AdminPasswordKey);
            return;
        }

        var admin = new FieldTrackUser { UserName = email, Email = email };
        EnsureSucceeded(await users.CreateAsync(admin, password));
        EnsureSucceeded(await users.AddToRoleAsync(admin, Roles.Admin));
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
}
