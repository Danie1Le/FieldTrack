using System.Globalization;
using System.Security.Cryptography;
using FieldTrack.Api.Auth;
using FieldTrack.Infrastructure.Messaging;
using FieldTrack.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FieldTrack.IntegrationTests;

/// <summary>
/// The real API host, configured to use the test containers. It runs in Development, the
/// environment that applies migrations on startup, so tests cover that path too.
/// </summary>
public sealed class FieldTrackApiFactory(string databaseConnectionString, string brokerHost, int brokerPort)
    : WebApplicationFactory<Program>
{
    /// <summary>Configuration key for the broker port, for tests that point a derived host elsewhere.</summary>
    public const string BrokerPortKey = $"{BrokerOptions.SectionName}:{nameof(BrokerOptions.Port)}";

    /// <summary>Configuration key for the broker host.</summary>
    public const string BrokerHostKey = $"{BrokerOptions.SectionName}:{nameof(BrokerOptions.Host)}";

    /// <summary>The first Admin, seeded from <c>FIELDTRACK_ADMIN_EMAIL</c> and <c>FIELDTRACK_ADMIN_PASSWORD</c>.</summary>
    public const string AdminEmail = "admin@fieldtrack.test";

    /// <summary>The first Admin's password.</summary>
    public const string AdminPassword = "Admin-test-pass-1";

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting($"ConnectionStrings:{PersistenceServiceCollectionExtensions.ConnectionStringName}", databaseConnectionString);
        builder.UseSetting(BrokerHostKey, brokerHost);
        builder.UseSetting(BrokerPortKey, brokerPort.ToString(CultureInfo.InvariantCulture));
        builder.UseSetting($"{AuthOptions.SectionName}:{nameof(AuthOptions.SigningKey)}", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        // Every test signs in from the same address; the rate limit gets its own test with a lower limit.
        builder.UseSetting($"{AuthOptions.SectionName}:{nameof(AuthOptions.SignInPermitsPerMinute)}", "10000");
        builder.UseSetting(UserSeeder.AdminEmailKey, AdminEmail);
        builder.UseSetting(UserSeeder.AdminPasswordKey, AdminPassword);
    }
}
