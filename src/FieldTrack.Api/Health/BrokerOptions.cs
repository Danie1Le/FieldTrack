using System.ComponentModel.DataAnnotations;

namespace FieldTrack.Api.Health;

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
}
