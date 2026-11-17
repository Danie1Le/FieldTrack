namespace FieldTrack.Infrastructure.Persistence;

/// <summary>
/// The stored rule state of one Alert Rule for one Asset (ADR 0005), the row form of
/// <see cref="Domain.Alerts.RuleState"/>. Ingest loads and writes it with SQL inside its batch transaction;
/// mapped here only so migrations own the table. The last evaluated Reading is kept as three plain columns, not
/// a complex property, because no EF code reads this row.
/// </summary>
public sealed class AlertRuleState
{
    /// <summary>With <see cref="AssetId"/>, the key: one state row per rule and Asset it covers.</summary>
    public Guid AlertRuleId { get; set; }

    /// <summary>With <see cref="AlertRuleId"/>, the key; a group rule has one row per Asset of the group.</summary>
    public Guid AssetId { get; set; }

    /// <summary>Whether the condition held at the last evaluated Reading.</summary>
    public bool Breached { get; set; }

    /// <summary>The last evaluated Reading's Device, so a straggler from it is recognised.</summary>
    public Guid LastDeviceId { get; set; }

    /// <summary>The last evaluated Reading's Boot.</summary>
    public int LastBoot { get; set; }

    /// <summary>The last evaluated Reading's Sequence number.</summary>
    public long LastSequenceNumber { get; set; }
}
