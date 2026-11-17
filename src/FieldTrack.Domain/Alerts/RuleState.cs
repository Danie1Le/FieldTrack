namespace FieldTrack.Domain.Alerts;

/// <summary>
/// What evaluation remembers for one Alert Rule and Asset between Readings (ADR 0005). An Alert is raised only
/// when <see cref="Breached"/> goes from false to true, so a user who resolves an Alert while the condition holds
/// sees nothing new until it clears and breaches again (the latch, #10).
/// </summary>
/// <param name="Breached">Whether the rule's condition held at the last evaluated Reading.</param>
/// <param name="LastEvaluated">
/// The last Reading evaluated; only Readings that come after it are evaluated. Null when a sweep set the state before
/// any Reading was evaluated: Offline rules never order Readings, so they leave it as it was.
/// </param>
public sealed record RuleState(bool Breached, ReadingId? LastEvaluated);
