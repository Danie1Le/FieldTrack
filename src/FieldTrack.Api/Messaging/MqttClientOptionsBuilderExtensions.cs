using FieldTrack.Infrastructure.Messaging;
using MQTTnet;
using MQTTnet.Formatter;

namespace FieldTrack.Api.Messaging;

/// <summary>How every FieldTrack MQTT client reaches the broker, so the health check and ingest connect alike.</summary>
public static class MqttClientOptionsBuilderExtensions
{
    /// <summary>MQTT 5 over TCP to <paramref name="broker"/>, with its credentials when they are configured.</summary>
    public static MqttClientOptionsBuilder WithBroker(this MqttClientOptionsBuilder builder, BrokerOptions broker)
    {
        builder.WithTcpServer(broker.Host, broker.Port).WithProtocolVersion(MqttProtocolVersion.V500);
        return string.IsNullOrEmpty(broker.Username) ? builder : builder.WithCredentials(broker.Username, broker.Password);
    }
}
