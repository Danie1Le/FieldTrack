using System.Globalization;
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

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting($"ConnectionStrings:{PersistenceServiceCollectionExtensions.ConnectionStringName}", databaseConnectionString);
        builder.UseSetting(BrokerHostKey, brokerHost);
        builder.UseSetting(BrokerPortKey, brokerPort.ToString(CultureInfo.InvariantCulture));
    }
}
