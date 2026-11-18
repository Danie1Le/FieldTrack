using FieldTrack.Domain.Alerts;

namespace FieldTrack.Infrastructure.Ingest;

/// <summary>What <see cref="AlertRuleRunner.EvaluateAsync"/> wrote in an ingest batch's transaction, for use once it commits.</summary>
/// <param name="States">The rule states that changed; pass them to <see cref="AlertRuleRunner.Keep"/> after commit.</param>
/// <param name="Raised">The ids of the Alerts raised, including any raised already Resolved (cleared in the same batch).</param>
/// <param name="Resolved">The ids of the Active Alerts the batch resolved.</param>
public sealed record RuleOutcome(
    IReadOnlyDictionary<(Guid Rule, Guid Asset), RuleState> States,
    IReadOnlyList<Guid> Raised,
    IReadOnlyList<Guid> Resolved);
