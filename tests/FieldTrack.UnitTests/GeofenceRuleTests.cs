using FieldTrack.Domain.Alerts;
using FieldTrack.Domain.Telemetry;

namespace FieldTrack.UnitTests;

public sealed class GeofenceRuleTests
{
    private static readonly Guid _asset = Guid.NewGuid();
    private static readonly Guid _device = Guid.NewGuid();
    private static readonly DateTimeOffset _t0 = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _now = new(2026, 11, 16, 12, 5, 0, TimeSpan.Zero);

    // Inside means north of the equator, so a Reading's latitude says which side it is on.
    private const double Inside = 10;
    private const double Outside = -10;

    private static readonly AlertRule _leftDepot = Rule(AlertRuleKind.GeofenceExit);
    private static readonly AlertRule _enteredDepot = Rule(AlertRuleKind.GeofenceEnter);

    [Theory]
    [InlineData(AlertRuleKind.GeofenceExit, Inside)]
    [InlineData(AlertRuleKind.GeofenceExit, Outside)]
    [InlineData(AlertRuleKind.GeofenceEnter, Inside)]
    [InlineData(AlertRuleKind.GeofenceEnter, Outside)]
    public void The_first_Reading_only_sets_the_baseline_on_either_side(AlertRuleKind kind, double latitude)
    {
        var evaluation = Evaluate(Rule(kind), null, At(1, 0, latitude));

        evaluation.Raised.ShouldBeEmpty();
        evaluation.ResolveActiveAlertAt.ShouldBeNull();
        // Breached means on the side the rule alerts on: outside for exit, inside for enter.
        var alertingSide = kind == AlertRuleKind.GeofenceExit ? latitude == Outside : latitude == Inside;
        evaluation.State.ShouldBe(new RuleState(alertingSide, new ReadingId(_device, 1, 0)));
    }

    [Fact]
    public void An_exit_rule_raises_when_an_Asset_seen_inside_is_seen_outside()
    {
        var outside = At(1, 1, Outside);

        var evaluation = Evaluate(_leftDepot, Clear(1, 0), outside);

        var alert = evaluation.Raised.ShouldHaveSingleItem();
        alert.AlertRuleId.ShouldBe(_leftDepot.Id);
        alert.AssetId.ShouldBe(_asset);
        alert.Reading.ShouldBe(new ReadingId(_device, 1, 1));
        alert.Message.ShouldBe("Left Geofence Depot");
        alert.Status.ShouldBe(AlertStatus.Open);
        alert.RaisedAt.ShouldBe(outside.Reading.DeviceTime);
        alert.CreatedAt.ShouldBe(_now);
        evaluation.State.ShouldBe(new RuleState(true, new ReadingId(_device, 1, 1)));
    }

    [Fact]
    public void An_exit_Alert_resolves_itself_when_the_Asset_is_back_inside()
    {
        var back = At(1, 2, Inside);

        var evaluation = Evaluate(_leftDepot, Breached(1, 1), back);

        evaluation.Raised.ShouldBeEmpty();
        evaluation.ResolveActiveAlertAt.ShouldBe(back.Reading.DeviceTime);
        evaluation.State.ShouldBe(new RuleState(false, new ReadingId(_device, 1, 2)));
    }

    [Fact]
    public void An_enter_rule_raises_when_an_Asset_seen_outside_is_seen_inside_and_resolves_when_it_leaves()
    {
        var entered = Evaluate(_enteredDepot, Clear(1, 0), At(1, 1, Inside));
        entered.Raised.ShouldHaveSingleItem().Message.ShouldBe("Entered Geofence Depot");

        var left = At(1, 2, Outside);
        var evaluation = Evaluate(_enteredDepot, entered.State, left);

        evaluation.Raised.ShouldBeEmpty();
        evaluation.ResolveActiveAlertAt.ShouldBe(left.Reading.DeviceTime);
    }

    [Fact]
    public void Staying_on_one_side_raises_and_resolves_nothing()
    {
        Evaluate(_leftDepot, Clear(1, 0), At(1, 1, Inside)).Raised.ShouldBeEmpty();

        var stillOut = Evaluate(_leftDepot, Breached(1, 0), At(1, 1, Outside));

        stillOut.Raised.ShouldBeEmpty();
        stillOut.ResolveActiveAlertAt.ShouldBeNull();
    }

    [Fact]
    public void A_first_Reading_outside_then_in_then_out_raises_only_on_the_observed_exit()
    {
        var evaluation = Evaluate(_leftDepot, null, At(1, 0, Outside), At(1, 1, Inside), At(1, 2, Outside));

        var alert = evaluation.Raised.ShouldHaveSingleItem();
        alert.Reading.ShouldBe(new ReadingId(_device, 1, 2));
        alert.Status.ShouldBe(AlertStatus.Open);
    }

    [Fact]
    public void An_exit_and_return_inside_one_backlog_is_raised_and_resolved_from_backlog()
    {
        var evaluation = Evaluate(_leftDepot, Clear(1, 0), At(1, 2, Inside), At(1, 1, Outside));

        var alert = evaluation.Raised.ShouldHaveSingleItem();
        alert.RaisedAt.ShouldBe(At(1, 1, Outside).Reading.DeviceTime);
        alert.Status.ShouldBe(AlertStatus.Resolved);
        alert.ResolvedAt.ShouldBe(At(1, 2, Inside).Reading.DeviceTime);
        alert.ResolveReason.ShouldBe(ResolveReason.Cleared);
        alert.FromBacklog.ShouldBeTrue();
    }

    [Fact]
    public void A_Geofence_rule_without_its_Geofence_evaluates_nothing()
    {
        var rule = Rule(AlertRuleKind.GeofenceExit);
        rule.Geofence = null;

        Evaluate(rule, Clear(1, 0), At(1, 1, Outside)).State.ShouldBe(Clear(1, 0));
    }

    private static RuleEvaluation Evaluate(AlertRule rule, RuleState? state, params StoredReading[] readings) =>
        RuleEvaluator.Evaluate(rule, _asset, attachedDeviceId: null, state, readings, new FixedTime(_now));

    private static AlertRule Rule(AlertRuleKind kind) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Rule",
        Kind = kind,
        AssetId = _asset,
        Severity = Severity.Warning,
        Enabled = true,
        GeofenceId = Guid.NewGuid(),
        Geofence = new NorthernHemisphere(),
    };

    private static RuleState Clear(int boot, long sequenceNumber) => new(false, new ReadingId(_device, boot, sequenceNumber));

    private static RuleState Breached(int boot, long sequenceNumber) => new(true, new ReadingId(_device, boot, sequenceNumber));

    // Device time rises with the Sequence number, so tests can find a Reading's time from its identity.
    private static StoredReading At(int boot, long sequenceNumber, double latitude) =>
        new(_device, new Reading(boot, sequenceNumber, _t0.AddHours(boot).AddSeconds(sequenceNumber), latitude, -0.12, null, null, null, null, null));

    private sealed class NorthernHemisphere : IGeofence
    {
        public string Name => "Depot";

        public bool Contains(double latitude, double longitude) => latitude > 0;
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
