namespace FieldTrack.Simulator;

/// <summary>
/// Where the simulated Devices publish: an <c>mqtt://</c> (plain TCP), <c>ws://</c> or <c>wss://</c> (MQTT over WebSockets)
/// address, and the user to connect as. WebSockets with TLS is how the Container Apps broker is reached (#154).
/// </summary>
/// <param name="Address">The broker's address; a WebSockets address includes its path.</param>
/// <param name="Username">The broker user, or null to connect anonymously.</param>
/// <param name="Password">The broker user's password. A secret: it comes from the environment, never the command line.</param>
internal sealed record MqttBroker(Uri Address, string? Username = null, string? Password = null)
{
    /// <summary>An anonymous broker over plain TCP, the local compose stack's.</summary>
    public static MqttBroker Tcp(string host, int port) => new(new UriBuilder("mqtt", host, port).Uri);

    /// <summary>The address only, so a logged run never shows the password.</summary>
    public override string ToString() => Address.ToString();
}
