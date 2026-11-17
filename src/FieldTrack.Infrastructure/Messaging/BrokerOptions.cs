using System.ComponentModel.DataAnnotations;
using System.Net;

namespace FieldTrack.Infrastructure.Messaging;

/// <summary>Where the MQTT broker listens (configuration section <c>Broker</c>).</summary>
public sealed class BrokerOptions : IValidatableObject
{
    /// <summary>The configuration section these options bind to.</summary>
    public const string SectionName = "Broker";

    // The broker's service name in docker-compose.yml: traffic to it stays on the compose network.
    private const string ComposeServiceName = "mosquitto";

    /// <summary>Broker host name or address.</summary>
    [Required]
    public string Host { get; set; } = "";

    /// <summary>Broker TCP port.</summary>
    [Range(1, 65535)]
    public int Port { get; set; } = 1883;

    /// <summary>Whether to connect over TLS. Credentials are sent only over TLS unless the broker is the local stack.</summary>
    public bool UseTls { get; set; }

    /// <summary>
    /// User name to connect with, or empty to connect anonymously. The client does not insist on credentials:
    /// a deployed broker must refuse anonymous clients itself (#61).
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Password for <see cref="Username"/>. A secret: supply it from the environment, user-secrets or Key Vault,
    /// never from a tracked file (NFR-8).
    /// </summary>
    public string? Password { get; set; }

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!string.IsNullOrEmpty(Password) && string.IsNullOrEmpty(Username))
        {
            yield return new ValidationResult("Broker:Password needs Broker:Username.", [nameof(Password)]);
        }

        if (!string.IsNullOrEmpty(Username) && !UseTls && !IsLocal(Host))
        {
            yield return new ValidationResult(
                "Broker credentials are sent only over TLS: set Broker:UseTls, or use a loopback or local compose broker.",
                [nameof(UseTls)]);
        }
    }

    private static bool IsLocal(string host) =>
        host is "localhost" or ComposeServiceName || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
}
