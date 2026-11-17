namespace FieldTrack.Domain.Alerts;

/// <summary>What evaluating one Alert Rule against one Asset's new Readings changes.</summary>
/// <param name="State">The rule state to keep; the state passed in (null if there was none) when no Reading was evaluated.</param>
/// <param name="ResolveActiveAlertAt">
/// When set, the condition, breached since before these Readings, cleared at this Device time: resolve the Alert
/// Active for this rule and Asset as Cleared. There may be none, after a manual resolve.
/// </param>
/// <param name="Raised">Alerts to store, in Reading order; all but the last are already Resolved.</param>
public sealed record RuleEvaluation(RuleState? State, DateTimeOffset? ResolveActiveAlertAt, IReadOnlyList<Alert> Raised);
