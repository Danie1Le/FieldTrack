namespace FieldTrack.Domain.Alerts;

/// <summary>
/// A record that an Alert Rule's condition became true for one Asset. An Asset has at most one Active Alert per
/// Alert Rule (#10); a Resolved Alert never reopens.
/// </summary>
public sealed class Alert
{
    /// <summary>Surrogate key.</summary>
    public Guid Id { get; set; }

    /// <summary>The Alert Rule that raised it.</summary>
    public Guid AlertRuleId { get; set; }

    /// <summary>The Asset it is about.</summary>
    public Guid AssetId { get; set; }

    /// <summary>The Reading that raised it.</summary>
    public required ReadingId Reading { get; set; }

    /// <summary>The rule's Severity when it was raised.</summary>
    public Severity Severity { get; set; }

    /// <summary>What happened, for people, such as "Engine temp 108.4 °C, above 105 (clears below 100)".</summary>
    public required string Message { get; set; }

    /// <summary>Where the Alert is in its lifecycle.</summary>
    public AlertStatus Status { get; set; }

    /// <summary>Device time of the Reading that raised it (ADR 0005), so a backlog's Alert shows when it happened.</summary>
    public DateTimeOffset RaisedAt { get; set; }

    /// <summary>When it was Resolved; for the platform, the Device time of the Reading that cleared it. Null while Active.</summary>
    public DateTimeOffset? ResolvedAt { get; set; }

    /// <summary>Why it was Resolved; null while Active.</summary>
    public ResolveReason? ResolveReason { get; set; }

    /// <summary>When the platform created it, for measuring alert latency against <see cref="RaisedAt"/>.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// True when one ingest batch both raised and resolved it, as when a Coverage gap's backlog arrives: the
    /// condition came and went while nobody could see it, so it was never Active.
    /// </summary>
    public bool FromBacklog { get; set; }
}
