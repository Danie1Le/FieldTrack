using FieldTrack.Api.Messaging;
using FieldTrack.Infrastructure.Messaging;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Exceptions;

namespace FieldTrack.Api.Health;

/// <summary>
/// Reports whether the MQTT broker accepts connections, by opening and closing a throwaway
/// MQTT 5 session. It uses its own random ClientId so it never touches the ingest worker's
/// persistent session (ADR 0004).
/// </summary>
public sealed class BrokerHealthCheck(IOptions<BrokerOptions> options) : IHealthCheck
{
    private static readonly TimeSpan _connectTimeout = TimeSpan.FromSeconds(3);

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var broker = options.Value;
        var clientOptions = new MqttClientOptionsBuilder()
            .WithBroker(broker)
            .WithClientId($"fieldtrack-health-{Guid.NewGuid():N}")
            .WithCleanStart()
            .WithTimeout(_connectTimeout)
            .Build();

        using var client = new MqttClientFactory().CreateMqttClient();
        try
        {
            await client.ConnectAsync(clientOptions, cancellationToken);
            await client.DisconnectAsync(cancellationToken: cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (MqttCommunicationException exception)
        {
            return HealthCheckResult.Unhealthy($"MQTT broker at {broker.Host}:{broker.Port} is unreachable.", exception);
        }
    }
}
