namespace FieldTrack.Domain.Alerts;

/// <summary>Why an Alert was Resolved (#10).</summary>
public enum ResolveReason
{
    /// <summary>The platform saw the condition clear.</summary>
    Cleared,

    /// <summary>A user resolved it by hand.</summary>
    Manual,

    /// <summary>Its Alert Rule was edited.</summary>
    RuleChanged,

    /// <summary>Its Alert Rule was disabled.</summary>
    RuleDisabled,

    /// <summary>Its Asset was Archived.</summary>
    AssetArchived,

    /// <summary>The Asset's Device was detached.</summary>
    DeviceDetached,
}
