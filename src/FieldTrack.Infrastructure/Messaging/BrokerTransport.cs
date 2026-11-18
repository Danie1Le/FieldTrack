namespace FieldTrack.Infrastructure.Messaging;

/// <summary>How MQTT reaches the broker; <see cref="BrokerOptions.UseTls"/> adds TLS to either.</summary>
public enum BrokerTransport
{
    /// <summary>MQTT over TCP, as in the local compose stack.</summary>
    Tcp,

    /// <summary>
    /// MQTT over WebSockets at <see cref="BrokerOptions.WebSocketPath"/>. With TLS (<c>wss</c>) it is how the Container Apps broker is
    /// reached, because HTTP ingress is the only way in without a VNet (#154).
    /// </summary>
    WebSockets,
}
