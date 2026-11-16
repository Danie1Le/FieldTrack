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
    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:FieldTrack", databaseConnectionString);
        builder.UseSetting("Broker:Host", brokerHost);
        builder.UseSetting("Broker:Port", brokerPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
