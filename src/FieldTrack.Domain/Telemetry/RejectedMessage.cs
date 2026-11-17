namespace FieldTrack.Domain.Telemetry;

/// <summary>
/// A message on a telemetry topic that ingest refused to store as a Reading, kept with the reason so that
/// stored plus rejected always equals published (spec #21) and an Admin can see what a Device sent.
/// </summary>
public sealed class RejectedMessage
{
    /// <summary>
    /// The longest <see cref="Topic"/> kept. A valid telemetry topic is under 100 characters, but the topic of an unknown
    /// Device may be as long as MQTT allows (65,535 bytes), so longer topics are clipped to keep each row small.
    /// </summary>
    public const int MaxTopicLength = 256;

    /// <summary>Surrogate key, rising in the order messages were rejected.</summary>
    public long Id { get; set; }

    /// <summary>The MQTT topic the message arrived on, which names the contract version and the Device.</summary>
    public required string Topic { get; set; }

    /// <summary>Whether <see cref="Topic"/> holds only the first <see cref="MaxTopicLength"/> characters of a longer topic.</summary>
    public bool TopicClipped { get; set; }

    /// <summary>The payload exactly as received, which need not be JSON or even UTF-8.</summary>
    public required byte[] Payload { get; set; }

    /// <summary>Why the message could not become a Reading.</summary>
    public RejectReason Reason { get; set; }

    /// <summary>When the platform received the message.</summary>
    public DateTimeOffset ReceivedTime { get; set; }
}
