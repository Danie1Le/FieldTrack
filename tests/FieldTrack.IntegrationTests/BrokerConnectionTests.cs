using System.ComponentModel.DataAnnotations;
using System.Text;
using DotNet.Testcontainers.Builders;
using FieldTrack.Api.Health;
using FieldTrack.Api.Messaging;
using FieldTrack.Infrastructure.Messaging;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using MQTTnet;

namespace FieldTrack.IntegrationTests;

public sealed class BrokerConnectionTests(FieldTrackFixture fixture) : IntegrationTest(fixture)
{
    private const string UsernameKey = $"{BrokerOptions.SectionName}:{nameof(BrokerOptions.Username)}";
    private const string PasswordKey = $"{BrokerOptions.SectionName}:{nameof(BrokerOptions.Password)}";

    [Fact]
    public void A_client_sends_the_configured_credentials()
    {
        var built = new MqttClientOptionsBuilder()
            .WithBroker(new BrokerOptions { Host = "localhost", Username = "ingest", Password = "secret" })
            .Build();

        var credentials = built.Credentials.ShouldBeOfType<MqttClientCredentials>();
        credentials.GetUserName(built).ShouldBe("ingest");
        Encoding.UTF8.GetString(credentials.GetPassword(built)).ShouldBe("secret");
    }

    [Fact]
    public void A_client_without_a_user_name_connects_anonymously_over_plain_TCP()
    {
        var built = new MqttClientOptionsBuilder().WithBroker(new BrokerOptions { Host = "localhost" }).Build();

        built.Credentials.ShouldBeNull();
        built.ChannelOptions.TlsOptions.UseTls.ShouldBeFalse();
    }

    [Fact]
    public void A_client_uses_TLS_when_configured() =>
        new MqttClientOptionsBuilder().WithBroker(new BrokerOptions { Host = "broker.example", UseTls = true }).Build()
            .ChannelOptions.TlsOptions.UseTls.ShouldBeTrue();

    [Fact]
    public void A_client_uses_MQTT_over_WebSockets_with_TLS_when_configured()
    {
        var built = new MqttClientOptionsBuilder()
            .WithBroker(new BrokerOptions { Host = "broker.example", Port = 443, Transport = BrokerTransport.WebSockets, UseTls = true })
            .Build();

        var webSocket = built.ChannelOptions.ShouldBeOfType<MqttClientWebSocketOptions>();
        new Uri(webSocket.Uri).ShouldBe(new Uri("wss://broker.example:443/mqtt"));
        webSocket.TlsOptions.UseTls.ShouldBeTrue();
    }

    [Fact]
    public void A_client_pings_an_idle_connection_well_within_the_Container_Apps_ingress_timeout()
    {
        // Container Apps HTTP ingress drops a request (a WebSocket included) after 240 s; keep-alive traffic must come well before.
        var ingressTimeout = TimeSpan.FromSeconds(240);

        new MqttClientOptionsBuilder().WithBroker(new BrokerOptions { Host = "broker.example", Transport = BrokerTransport.WebSockets }).Build()
            .KeepAlivePeriod.ShouldBe(MqttClientOptionsBuilderExtensions.KeepAlive);
        MqttClientOptionsBuilderExtensions.KeepAlive.ShouldBeLessThanOrEqualTo(ingressTimeout / 4);
    }

    [Theory]
    [InlineData("broker.example", false, false)]
    [InlineData("broker.example", true, true)]
    [InlineData("localhost", false, true)]
    [InlineData("127.0.0.1", false, true)]
    [InlineData("::1", false, true)]
    [InlineData("mosquitto", false, true)]
    public void Credentials_go_over_plain_TCP_only_to_a_local_broker(string host, bool useTls, bool valid) =>
        IsValid(new BrokerOptions { Host = host, UseTls = useTls, Username = "ingest", Password = "secret" }).ShouldBe(valid);

    [Fact]
    public void A_password_without_a_user_name_is_invalid() =>
        IsValid(new BrokerOptions { Host = "localhost", Password = "secret" }).ShouldBeFalse();

    [Fact]
    public async Task Startup_fails_when_a_password_has_no_user_name()
    {
        await using var api = Fixture.Api.WithWebHostBuilder(builder => builder.UseSetting(PasswordKey, "secret"));

        Should.Throw<OptionsValidationException>(() => api.CreateClient());
    }

    [Fact]
    public async Task Startup_fails_when_credentials_would_go_to_a_remote_broker_without_TLS()
    {
        await using var api = Fixture.Api.WithWebHostBuilder(builder => builder
            .UseSetting(FieldTrackApiFactory.BrokerHostKey, "broker.example")
            .UseSetting(UsernameKey, "ingest")
            .UseSetting(PasswordKey, "secret"));

        Should.Throw<OptionsValidationException>(() => api.CreateClient());
    }

    [Fact]
    public async Task Health_says_the_broker_refused_the_connection_when_it_rejects_the_credentials()
    {
        // A broker that refuses every client, so any credentials are wrong.
        await using var broker = new ContainerBuilder(TestImages.Mosquitto)
            .WithPortBinding(1883, assignRandomHostPort: true)
            .WithResourceMapping(Encoding.UTF8.GetBytes("listener 1883\nallow_anonymous false\nlog_type information\n"), "/mosquitto/config/mosquitto.conf")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("mosquitto version .+ running"))
            .Build();
        await broker.StartAsync(CancellationToken);
        var check = new BrokerHealthCheck(Options.Create(new BrokerOptions
        {
            Host = broker.Hostname,
            Port = broker.GetMappedPublicPort(1883),
            Username = "ingest",
            Password = "wrong",
        }));

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken);

        result.Status.ShouldBe(HealthStatus.Unhealthy);
        result.Description.ShouldBe($"MQTT broker at {broker.Hostname}:{broker.GetMappedPublicPort(1883)} refused the connection: NotAuthorized.");
    }

    private static bool IsValid(BrokerOptions options) =>
        Validator.TryValidateObject(options, new ValidationContext(options), [], validateAllProperties: true);
}
