namespace FieldTrack.Infrastructure.Persistence;

/// <summary>
/// The stored rule state of one Alert Rule for one Asset (ADR 0005), the row form of
/// <see cref="Domain.Alerts.RuleState"/>. Ingest loads and writes it with SQL inside its batch transaction;
/// mapped here only so migrations own the table.
/// </summary>
public sealed class AlertRuleState
{
    /// <summary>The Alert Rule.</summary>
    public Guid AlertRuleId { get; set; }

    /// <summary>The Asset.</summary>
    public Guid AssetId { get; set; }

    /// <summary>Whether the condition held at the last evaluated Reading.</summary>
    public bool Breached { get; set; }

    /// <summary>The last evaluated Reading's Device.</summary>
    public Guid LastDeviceId { get; set; }

    /// <summary>The last evaluated Reading's Boot.</summary>
    public int LastBoot { get; set; }

    /// <summary>The last evaluated Reading's Sequence number.</summary>
    public long LastSequenceNumber { get; set; }
}
