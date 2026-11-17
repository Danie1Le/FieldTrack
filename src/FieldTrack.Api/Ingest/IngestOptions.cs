using System.ComponentModel.DataAnnotations;

namespace FieldTrack.Api.Ingest;

/// <summary>Settings of the ingest worker (configuration section <c>Ingest</c>).</summary>
public sealed class IngestOptions
{
    /// <summary>The configuration section these options bind to.</summary>
    public const string SectionName = "Ingest";

    /// <summary>
    /// The worker's fixed MQTT ClientId. The broker keeps the session and its unacknowledged messages under
    /// this name across restarts (ADR 0004), so a change of name starts from an empty session.
    /// </summary>
    [Required]
    public string ClientId { get; set; } = "fieldtrack-ingest";
}
