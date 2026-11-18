using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using FieldTrack.Infrastructure.Messaging;
using FieldTrack.Simulator;

namespace FieldTrack.IntegrationTests;

/// <summary>
/// The broker image Container Apps runs (<c>docker/mosquitto/cloud</c>): built from its Dockerfile and started with passwords
/// made up for this run, which its entrypoint turns into the password file. It listens for MQTT over plain WebSockets only:
/// in Azure the ingress terminates TLS in front of it (#154).
/// </summary>
public sealed class CloudBroker : IAsyncLifetime
{
    private const ushort WebSocketPort = 8080;

    private IFutureDockerImage? _image;
    private IContainer? _container;

    /// <summary>The ingest user's password, as the deployment would supply it.</summary>
    public string IngestPassword { get; } = Guid.NewGuid().ToString("N");

    /// <summary>The simulator user's password.</summary>
    public string SimulatorPassword { get; } = Guid.NewGuid().ToString("N");

    /// <summary>Broker settings for the API: WebSockets, signed in as the ingest user.</summary>
    public BrokerOptions Ingest => new()
    {
        Host = Container.Hostname,
        Port = Container.GetMappedPublicPort(WebSocketPort),
        Transport = BrokerTransport.WebSockets,
        Username = "ingest",
        Password = IngestPassword,
    };

    /// <summary>The broker as the simulator reaches it: WebSockets, signed in as the simulator user.</summary>
    internal MqttBroker Simulator =>
        new(new UriBuilder("ws", Container.Hostname, Container.GetMappedPublicPort(WebSocketPort), Ingest.WebSocketPath).Uri, "simulator", SimulatorPassword);

    private IContainer Container => _container ?? throw new InvalidOperationException("The broker has not started.");

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        _image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(Path.Combine(AppContext.BaseDirectory, "mosquitto-cloud"))
            .WithDockerfile("Dockerfile")
            .Build();
        await _image.CreateAsync();
        _container = new ContainerBuilder(_image)
            .WithPortBinding(WebSocketPort, assignRandomHostPort: true)
            .WithEnvironment("MQTT_INGEST_PASSWORD", IngestPassword)
            .WithEnvironment("MQTT_SIMULATOR_PASSWORD", SimulatorPassword)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged(
                "mosquitto version .+ running", wait => wait.WithTimeout(TimeSpan.FromMinutes(1))))
            .Build();
        await _container.StartAsync();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }

        if (_image is not null)
        {
            await _image.DisposeAsync();
        }
    }
}
