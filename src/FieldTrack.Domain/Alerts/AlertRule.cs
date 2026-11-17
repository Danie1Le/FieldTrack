namespace FieldTrack.Domain.Alerts;

/// <summary>
/// A condition an Admin sets up for one Asset or every Asset in an Asset Group; ingest evaluates it against each
/// new Reading (ADR 0005). The settings a kind does not use stay null.
/// </summary>
public sealed class AlertRule
{
    /// <summary>Surrogate key.</summary>
    public Guid Id { get; set; }

    /// <summary>The name people know the rule by, such as "Engine overheat".</summary>
    public required string Name { get; set; }

    /// <summary>What the rule watches, which says which of the settings below it uses.</summary>
    public AlertRuleKind Kind { get; set; }

    /// <summary>The one Asset the rule covers, or null when it covers an Asset Group.</summary>
    public Guid? AssetId { get; set; }

    /// <summary>The Asset Group whose every Asset the rule covers, or null when it covers one Asset.</summary>
    public Guid? AssetGroupId { get; set; }

    /// <summary>Copied to each Alert the rule raises.</summary>
    public Severity Severity { get; set; }

    /// <summary>Only enabled rules are evaluated, so an Admin can pause a rule without deleting it.</summary>
    public bool Enabled { get; set; }

    /// <summary>Threshold rules: the sensor watched.</summary>
    public Sensor? Sensor { get; set; }

    /// <summary>Threshold rules: which side of <see cref="Threshold"/> breaches.</summary>
    public ThresholdOperator? Operator { get; set; }

    /// <summary>Threshold rules: the value the sensor must pass to breach, in the sensor's unit.</summary>
    public double? Threshold { get; set; }

    /// <summary>Threshold rules: how far back past <see cref="Threshold"/> the value must move to clear (0 or more).</summary>
    public double? Hysteresis { get; set; }

    /// <summary>Whether the rule covers the Asset <paramref name="assetId"/>, which is in Asset Group <paramref name="assetGroupId"/> (or none).</summary>
    public bool Targets(Guid assetId, Guid? assetGroupId) =>
        AssetId == assetId || (AssetGroupId is not null && AssetGroupId == assetGroupId);
}
