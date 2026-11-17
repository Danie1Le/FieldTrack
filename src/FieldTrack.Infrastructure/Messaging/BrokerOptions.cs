using System.ComponentModel.DataAnnotations;

namespace FieldTrack.Infrastructure.Messaging;

/// <summary>Where the MQTT broker listens (configuration section <c>Broker</c>).</summary>
public sealed class BrokerOptions
{
    /// <summary>The configuration section these options bind to.</summary>
    public const string SectionName = "Broker";

    /// <summary>Broker host name or address.</summary>
    [Required]
    public string Host { get; set; } = "";

    /// <summary>Broker TCP port for plain MQTT.</summary>
    [Range(1, 65535)]
    public int Port { get; set; } = 1883;

    /// <summary>
    /// User name to connect with, or empty to connect anonymously, which only the local stack allows
    /// (#61: a deployed broker requires authentication).
    /// </summary>
    public string? Username { get; set; }

    /// <summary>Password for <see cref="Username"/>; a secret, so it comes from user-secrets or Key Vault, never a file.</summary>
    public string? Password { get; set; }
}
