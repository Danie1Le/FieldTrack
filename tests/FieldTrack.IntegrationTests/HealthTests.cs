using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace FieldTrack.IntegrationTests;

public sealed class HealthTests(FieldTrackFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Health_reports_database_and_broker_reachable()
    {
        using var client = Fixture.Api.CreateClient();

        using var response = await client.GetAsync("/health", CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await ReadJsonAsync(response);
        body.GetProperty("status").GetString().ShouldBe("Healthy");
        body.GetProperty("checks").GetProperty("database").GetString().ShouldBe("Healthy");
        body.GetProperty("checks").GetProperty("broker").GetString().ShouldBe("Healthy");
    }

    [Fact]
    public async Task Health_reports_unhealthy_when_the_broker_is_unreachable()
    {
        // A bound socket that never listens: connections to its port are refused, and no other process can take the port.
        using var closedPort = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        closedPort.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)closedPort.LocalEndPoint!).Port;

        await using var api = Fixture.Api.WithWebHostBuilder(builder => builder
            .UseSetting(FieldTrackApiFactory.BrokerHostKey, IPAddress.Loopback.ToString())
            .UseSetting(FieldTrackApiFactory.BrokerPortKey, port.ToString(CultureInfo.InvariantCulture)));
        using var client = api.CreateClient();

        using var response = await client.GetAsync("/health", CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var body = await ReadJsonAsync(response);
        body.GetProperty("status").GetString().ShouldBe("Unhealthy");
        body.GetProperty("checks").GetProperty("database").GetString().ShouldBe("Healthy");
        body.GetProperty("checks").GetProperty("broker").GetString().ShouldBe("Unhealthy");
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken));
        return document.RootElement.Clone();
    }
}
