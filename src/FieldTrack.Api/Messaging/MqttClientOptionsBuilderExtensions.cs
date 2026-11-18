using FieldTrack.Infrastructure.Messaging;
using MQTTnet;
using MQTTnet.Formatter;

namespace FieldTrack.Api.Messaging;

/// <summary>How every FieldTrack MQTT client reaches the broker, so the health check and ingest connect alike.</summary>
public static class MqttClientOptionsBuilderExtensions
{
    /// <summary>
    /// How often a client with nothing to send pings the broker. Container Apps HTTP ingress drops a request, a WebSocket included,
    /// after 240 s (#154); the ping keeps an idle connection well inside that, and shows a dead one within 1.5 periods.
    /// </summary>
    public static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(15);

    /// <summary>MQTT 5 to <paramref name="broker"/> over TCP or WebSockets, over TLS and with its credentials when they are configured.</summary>
    public static MqttClientOptionsBuilder WithBroker(this MqttClientOptionsBuilder builder, BrokerOptions broker)
    {
        if (broker.Transport == BrokerTransport.WebSockets)
        {
            var address = new UriBuilder(broker.UseTls ? "wss" : "ws", broker.Host, broker.Port, broker.WebSocketPath).Uri;
            builder.WithWebSocketServer(webSocket => webSocket.WithUri(address.ToString()));
        }
        else
        {
            builder.WithTcpServer(broker.Host, broker.Port);
        }

        builder.WithProtocolVersion(MqttProtocolVersion.V500).WithKeepAlivePeriod(KeepAlive);
        if (broker.UseTls)
        {
            builder.WithTlsOptions(tls => tls.UseTls());
        }

        return string.IsNullOrEmpty(broker.Username) ? builder : builder.WithCredentials(broker.Username, broker.Password);
    }
}
