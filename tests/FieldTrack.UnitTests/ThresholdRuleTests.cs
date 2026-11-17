using FieldTrack.Domain.Alerts;
using FieldTrack.Domain.Telemetry;

namespace FieldTrack.UnitTests;

public sealed class ThresholdRuleTests
{
    private static readonly Guid _asset = Guid.NewGuid();
    private static readonly Guid _device = Guid.NewGuid();
    private static readonly DateTimeOffset _t0 = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _now = new(2026, 11, 16, 12, 5, 0, TimeSpan.Zero);

    // ADR 0005's example: raise above 105 °C, clear below 100 °C.
    private static readonly AlertRule _overheat = new()
    {
        Id = Guid.NewGuid(),
        Name = "Engine overheat",
        Kind = AlertRuleKind.Threshold,
        AssetId = _asset,
        Severity = Severity.Critical,
        Enabled = true,
        Sensor = Sensor.EngineTempC,
        Operator = ThresholdOperator.GreaterThan,
        Threshold = 105,
        Hysteresis = 5,
    };

    private static readonly AlertRule _lowFuel = new()
    {
        Id = Guid.NewGuid(),
        Name = "Low fuel",
        Kind = AlertRuleKind.Threshold,
        AssetId = _asset,
        Severity = Severity.Warning,
        Enabled = true,
        Sensor = Sensor.FuelPct,
        Operator = ThresholdOperator.LessThan,
        Threshold = 10,
        Hysteresis = 5,
    };

    [Fact]
    public void A_first_Reading_past_the_threshold_raises_an_Open_Alert_because_unknown_state_counts_as_clear()
    {
        var reading = Temp(1, 0, 108.4f);

        var evaluation = Evaluate(_overheat, null, reading);

        var alert = evaluation.Raised.ShouldHaveSingleItem();
        alert.AlertRuleId.ShouldBe(_overheat.Id);
        alert.AssetId.ShouldBe(_asset);
        alert.Reading.ShouldBe(new ReadingId(_device, 1, 0));
        alert.Severity.ShouldBe(Severity.Critical);
        alert.Message.ShouldBe("Engine temp 108.4 °C, above 105 (clears below 100)");
        alert.Status.ShouldBe(AlertStatus.Open);
        alert.RaisedAt.ShouldBe(reading.Reading.DeviceTime);
        alert.CreatedAt.ShouldBe(_now);
        alert.ResolvedAt.ShouldBeNull();
        alert.ResolveReason.ShouldBeNull();
        alert.FromBacklog.ShouldBeFalse();
        evaluation.ActiveAlertClearedAt.ShouldBeNull();
        evaluation.State.ShouldBe(new RuleState(Breached: true, LastEvaluated: new ReadingId(_device, 1, 0)));
    }

    [Theory]
    [InlineData(105f)]
    [InlineData(20f)]
    public void A_value_not_past_the_threshold_raises_nothing_and_records_the_state_as_clear(float temp)
    {
        var evaluation = Evaluate(_overheat, null, Temp(1, 0, temp));

        evaluation.Raised.ShouldBeEmpty();
        evaluation.State.ShouldBe(new RuleState(false, new ReadingId(_device, 1, 0)));
    }

    [Theory]
    [InlineData(106f)]
    [InlineData(103f)]
    [InlineData(100f)]
    public void A_breached_greater_than_rule_stays_breached_until_the_value_drops_below_threshold_minus_Hysteresis(float temp)
    {
        var evaluation = Evaluate(_overheat, Breached(1, 0), Temp(1, 1, temp));

        evaluation.Raised.ShouldBeEmpty();
        evaluation.ActiveAlertClearedAt.ShouldBeNull();
        evaluation.State.ShouldBe(new RuleState(true, new ReadingId(_device, 1, 1)));
    }

    [Fact]
    public void A_breached_greater_than_rule_clears_below_threshold_minus_Hysteresis_and_resolves_the_Active_Alert_at_that_Device_time()
    {
        var reading = Temp(1, 1, 99.9f);

        var evaluation = Evaluate(_overheat, Breached(1, 0), reading);

        evaluation.Raised.ShouldBeEmpty();
        evaluation.ActiveAlertClearedAt.ShouldBe(reading.Reading.DeviceTime);
        evaluation.State.ShouldBe(new RuleState(false, new ReadingId(_device, 1, 1)));
    }

    [Fact]
    public void A_less_than_rule_raises_below_the_threshold_and_clears_only_above_threshold_plus_Hysteresis()
    {
        var low = Fuel(1, 0, 9.5f);
        var raised = Evaluate(_lowFuel, null, low);
        raised.Raised.ShouldHaveSingleItem().Message.ShouldBe("Fuel 9.5 %, below 10 (clears above 15)");

        var stillLow = Evaluate(_lowFuel, raised.State, Fuel(1, 1, 15f));
        stillLow.ActiveAlertClearedAt.ShouldBeNull();
        stillLow.State!.Breached.ShouldBeTrue();

        var refuelled = Fuel(1, 2, 15.5f);
        var cleared = Evaluate(_lowFuel, stillLow.State, refuelled);
        cleared.ActiveAlertClearedAt.ShouldBe(refuelled.Reading.DeviceTime);
        cleared.State!.Breached.ShouldBeFalse();
    }

    [Fact]
    public void A_further_breach_while_breached_raises_nothing_new()
    {
        var evaluation = Evaluate(_overheat, Breached(1, 0), Temp(1, 1, 120f));

        evaluation.Raised.ShouldBeEmpty();
        evaluation.State!.Breached.ShouldBeTrue();
    }

    [Fact]
    public void A_Reading_without_the_rules_sensor_changes_nothing()
    {
        var state = Breached(1, 0);
        var withoutTemp = new StoredReading(_device, Reading(1, 1) with { FuelPct = 50f });

        var evaluation = Evaluate(_overheat, state, withoutTemp);

        evaluation.State.ShouldBe(state);
        evaluation.Raised.ShouldBeEmpty();
        evaluation.ActiveAlertClearedAt.ShouldBeNull();
        Evaluate(_overheat, null, withoutTemp).State.ShouldBeNull();
    }

    [Theory]
    [InlineData(1, 4)]
    [InlineData(1, 5)]
    [InlineData(0, 9)]
    public void A_straggler_at_or_before_the_last_evaluated_Reading_is_not_evaluated(int boot, long sequenceNumber)
    {
        var state = Breached(1, 5);

        var evaluation = Evaluate(_overheat, state, Temp(boot, sequenceNumber, 20f));

        evaluation.State.ShouldBe(state);
        evaluation.ActiveAlertClearedAt.ShouldBeNull();
    }

    [Fact]
    public void A_new_Boot_counts_as_newer_whatever_its_Sequence_number()
    {
        var evaluation = Evaluate(_overheat, Breached(1, 5), Temp(2, 0, 20f));

        evaluation.State.ShouldBe(new RuleState(false, new ReadingId(_device, 2, 0)));
    }

    [Fact]
    public void A_Reading_from_a_different_Device_counts_as_newer()
    {
        var swapped = Guid.NewGuid();

        var evaluation = Evaluate(_overheat, Breached(7, 500), new StoredReading(swapped, Reading(1, 0) with { EngineTempC = 20f }));

        evaluation.State.ShouldBe(new RuleState(false, new ReadingId(swapped, 1, 0)));
    }

    [Fact]
    public void Readings_are_evaluated_in_Boot_and_Sequence_number_order_whatever_order_they_arrive_in()
    {
        // Arrival order would raise on seq 2 and clear on seq 1; Sequence number order clears at seq 1 then raises at seq 2.
        var evaluation = Evaluate(_overheat, Breached(1, 0), Temp(1, 2, 110f), Temp(1, 1, 90f));

        evaluation.ActiveAlertClearedAt.ShouldBe(Temp(1, 1, 90f).Reading.DeviceTime);
        evaluation.Raised.ShouldHaveSingleItem().Reading.ShouldBe(new ReadingId(_device, 1, 2));
        evaluation.State.ShouldBe(new RuleState(true, new ReadingId(_device, 1, 2)));
    }

    [Fact]
    public void A_backlog_breach_that_clears_inside_the_batch_is_raised_and_resolved_together_and_flagged_as_from_backlog()
    {
        var evaluation = Evaluate(_overheat, null, Temp(1, 0, 90f), Temp(1, 1, 107f), Temp(1, 2, 104f), Temp(1, 3, 95f), Temp(1, 4, 96f));

        var alert = evaluation.Raised.ShouldHaveSingleItem();
        alert.Reading.ShouldBe(new ReadingId(_device, 1, 1));
        alert.RaisedAt.ShouldBe(Temp(1, 1, 0).Reading.DeviceTime);
        alert.Status.ShouldBe(AlertStatus.Resolved);
        alert.ResolvedAt.ShouldBe(Temp(1, 3, 0).Reading.DeviceTime);
        alert.ResolveReason.ShouldBe(ResolveReason.Cleared);
        alert.FromBacklog.ShouldBeTrue();
        evaluation.ActiveAlertClearedAt.ShouldBeNull();
        evaluation.State.ShouldBe(new RuleState(false, new ReadingId(_device, 1, 4)));
    }

    [Fact]
    public void A_backlog_clears_the_Active_Alert_then_raises_and_resolves_each_later_breach_in_turn()
    {
        var evaluation = Evaluate(_overheat, Breached(1, 0),
            Temp(1, 1, 99f), Temp(1, 2, 106f), Temp(1, 3, 98f), Temp(1, 4, 111f));

        evaluation.ActiveAlertClearedAt.ShouldBe(Temp(1, 1, 0).Reading.DeviceTime);
        evaluation.Raised.Select(alert => (alert.Reading.SequenceNumber, alert.Status, alert.FromBacklog))
            .ShouldBe([(2L, AlertStatus.Resolved, true), (4L, AlertStatus.Open, false)]);
        evaluation.State!.Breached.ShouldBeTrue();
    }

    [Fact]
    public void A_rule_targets_its_own_Asset_or_every_Asset_in_its_Asset_Group()
    {
        var group = Guid.NewGuid();
        var groupRule = new AlertRule { Name = "Fleet overheat", AssetGroupId = group };

        _overheat.Targets(_asset, assetGroupId: null).ShouldBeTrue();
        _overheat.Targets(Guid.NewGuid(), assetGroupId: null).ShouldBeFalse();
        groupRule.Targets(Guid.NewGuid(), group).ShouldBeTrue();
        groupRule.Targets(Guid.NewGuid(), Guid.NewGuid()).ShouldBeFalse();
        groupRule.Targets(Guid.NewGuid(), assetGroupId: null).ShouldBeFalse();
    }

    private static RuleEvaluation Evaluate(AlertRule rule, RuleState? state, params StoredReading[] readings) =>
        RuleEvaluator.Evaluate(rule, _asset, state, readings, new FixedTime(_now));

    private static RuleState Breached(int boot, long sequenceNumber) => new(true, new ReadingId(_device, boot, sequenceNumber));

    // Device time rises with the Sequence number, so tests can find a Reading's time from its identity.
    private static Reading Reading(int boot, long sequenceNumber) =>
        new(boot, sequenceNumber, _t0.AddHours(boot).AddSeconds(sequenceNumber), 51.5, -0.12, null, null, null, null, null);

    private static StoredReading Temp(int boot, long sequenceNumber, float engineTempC) =>
        new(_device, Reading(boot, sequenceNumber) with { EngineTempC = engineTempC });

    private static StoredReading Fuel(int boot, long sequenceNumber, float fuelPct) =>
        new(_device, Reading(boot, sequenceNumber) with { FuelPct = fuelPct });

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
