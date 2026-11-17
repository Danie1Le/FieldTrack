using System.ComponentModel.DataAnnotations;
using FieldTrack.Domain.Alerts;

namespace FieldTrack.Api.Alerts;

/// <summary>An Alert Rule as an Admin creates or edits it. Exactly one of <paramref name="AssetId"/> and <paramref name="AssetGroupId"/> is set.</summary>
/// <param name="Name">The name people know the rule by.</param>
/// <param name="Kind">What the rule watches; Threshold rules need the four Threshold settings, Geofence rules a Geofence, Offline rules <paramref name="OfflineSeconds"/>.</param>
/// <param name="AssetId">The one Asset the rule covers.</param>
/// <param name="AssetGroupId">The Asset Group whose every Asset the rule covers.</param>
/// <param name="Severity">Copied to each Alert the rule raises.</param>
/// <param name="Enabled">Only enabled rules are evaluated; true when left out.</param>
/// <param name="Sensor">Threshold rules: the sensor watched.</param>
/// <param name="Operator">Threshold rules: <c>GreaterThan</c> or <c>LessThan</c>.</param>
/// <param name="Threshold">Threshold rules: the value the sensor must pass, in the sensor's unit.</param>
/// <param name="Hysteresis">Threshold rules: how far back past the threshold the value must move to clear; 0 or more.</param>
/// <param name="GeofenceId">Geofence exit and enter rules: the Geofence watched.</param>
/// <param name="OfflineSeconds">
/// Offline rules: how many seconds without a Reading raise the Alert. Whenever sent it must be 1 or more, like every
/// setting's range; other kinds then ignore it.
/// </param>
public sealed record AlertRuleRequest(
    [Required, MaxLength(200)] string Name,
    // Nullable so a missing value is refused instead of silently becoming the first enum value.
    [Required] AlertRuleKind? Kind,
    Guid? AssetId,
    Guid? AssetGroupId,
    [Required] Severity? Severity,
    Sensor? Sensor,
    ThresholdOperator? Operator,
    double? Threshold,
    [Range(0, double.MaxValue)] double? Hysteresis,
    Guid? GeofenceId,
    [Range(1, int.MaxValue)] int? OfflineSeconds,
    bool Enabled = true);

/// <summary>An Alert Rule as anyone signed in sees it.</summary>
/// <param name="Id">The rule's id.</param>
/// <param name="Name">The name people know the rule by.</param>
/// <param name="Kind">What the rule watches.</param>
/// <param name="AssetId">The one Asset the rule covers, or null.</param>
/// <param name="AssetGroupId">The Asset Group the rule covers, or null.</param>
/// <param name="Severity">Copied to each Alert the rule raises.</param>
/// <param name="Enabled">Whether the rule is evaluated.</param>
/// <param name="Sensor">Threshold rules: the sensor watched.</param>
/// <param name="Operator">Threshold rules: which side of the threshold breaches.</param>
/// <param name="Threshold">Threshold rules: the value the sensor must pass.</param>
/// <param name="Hysteresis">Threshold rules: how far back the value must move to clear.</param>
/// <param name="GeofenceId">Geofence exit and enter rules: the Geofence watched.</param>
/// <param name="OfflineSeconds">Offline rules: how many seconds without a Reading raise the Alert.</param>
public sealed record AlertRuleResponse(
    Guid Id,
    string Name,
    AlertRuleKind Kind,
    Guid? AssetId,
    Guid? AssetGroupId,
    Severity Severity,
    bool Enabled,
    Sensor? Sensor,
    ThresholdOperator? Operator,
    double? Threshold,
    double? Hysteresis,
    Guid? GeofenceId,
    int? OfflineSeconds);

/// <summary>An Alert as anyone signed in sees it.</summary>
/// <param name="Id">The Alert's id.</param>
/// <param name="AlertRuleId">The Alert Rule that raised it.</param>
/// <param name="AssetId">The Asset it is about.</param>
/// <param name="Reading">The Reading that raised it; null for an Offline Alert, which silence raised.</param>
/// <param name="Severity">How urgently it needs attention.</param>
/// <param name="Message">What happened, for people.</param>
/// <param name="Status">Open, Acknowledged or Resolved.</param>
/// <param name="RaisedAt">Device time of the Reading that raised it; for an Offline Alert, the time the platform noticed the silence.</param>
/// <param name="ResolvedAt">When it was Resolved, or null while Active.</param>
/// <param name="ResolveReason">Why it was Resolved, or null while Active.</param>
/// <param name="AcknowledgedBy">Display name of the user who Acknowledged it, or null. Never an email: only Admins see those.</param>
/// <param name="AcknowledgedAt">When it was Acknowledged, or null.</param>
/// <param name="ResolvedBy">Display name of the user who Resolved it by hand; null while Active and when the platform Resolved it.</param>
/// <param name="Note">What the user who Resolved it by hand wrote, or null.</param>
/// <param name="Latched">
/// The Latch (GLOSSARY, #10). On an Active Alert: its condition still holds, so Resolving it by hand latches the rule and the
/// UI warns. On a Resolved one: a user Resolved it while the condition held, so the rule raised nothing new until the
/// condition cleared and breached again.
/// </param>
/// <param name="CreatedAt">When the platform created it.</param>
/// <param name="FromBacklog">True when one ingest batch raised and resolved it, so it was never Active.</param>
public sealed record AlertResponse(
    Guid Id,
    Guid AlertRuleId,
    Guid AssetId,
    ReadingId? Reading,
    Severity Severity,
    string Message,
    AlertStatus Status,
    DateTimeOffset RaisedAt,
    DateTimeOffset? ResolvedAt,
    ResolveReason? ResolveReason,
    string? AcknowledgedBy,
    DateTimeOffset? AcknowledgedAt,
    string? ResolvedBy,
    string? Note,
    bool Latched,
    DateTimeOffset CreatedAt,
    bool FromBacklog);

/// <summary>How a user Resolves an Alert by hand.</summary>
/// <param name="Note">Optional, for the audit trail, such as what was done about it.</param>
public sealed record ResolveAlertRequest([MaxLength(Alert.MaxNoteLength)] string? Note);
