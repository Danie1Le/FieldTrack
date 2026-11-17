using FieldTrack.Domain.Alerts;
using FieldTrack.Domain.Telemetry;
using Microsoft.Extensions.Time.Testing;

namespace FieldTrack.UnitTests;

public sealed class OfflineRuleTests
{
    private static readonly Guid _asset = Guid.NewGuid();
    private static readonly Guid _device = Guid.NewGuid();
    private static readonly DateTimeOffset _attachedAt = new(2026, 11, 17, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _lastReceivedAt = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    private static readonly AlertRule _silent = new()
    {
        Id = Guid.NewGuid(),
        Name = "Generator silent",
        Kind = AlertRuleKind.Offline,
        AssetId = _asset,
        Severity = Severity.Critical,
        Enabled = true,
        OfflineSeconds = 60,
    };

    // The worker caught up with the broker backlog long before the silence began.
    private static readonly DateTimeOffset _caughtUpAt = _lastReceivedAt.AddHours(-1);

    private readonly FakeTimeProvider _time = new(_lastReceivedAt);

    [Theory]
    [InlineData(59, false)]
    [InlineData(60, true)]
    public void A_sweep_raises_once_the_Device_has_sent_nothing_for_the_rules_seconds(int silentSeconds, bool raises)
    {
        _time.Advance(TimeSpan.FromSeconds(silentSeconds));

        var evaluation = Sweep(null, Tick(_lastReceivedAt));

        evaluation.Raised.Count.ShouldBe(raises ? 1 : 0);
        evaluation.ResolveActiveAlertAt.ShouldBeNull();
    }

    [Fact]
    public void The_Offline_Alert_is_raised_at_the_sweep_time_with_no_Reading()
    {
        var state = new RuleState(false, new ReadingId(_device, 1, 7));
        _time.Advance(TimeSpan.FromSeconds(61));

        var evaluation = Sweep(state, Tick(_lastReceivedAt));

        var alert = evaluation.Raised.ShouldHaveSingleItem();
        alert.AlertRuleId.ShouldBe(_silent.Id);
        alert.AssetId.ShouldBe(_asset);
        alert.Reading.ShouldBeNull();
        alert.Severity.ShouldBe(Severity.Critical);
        alert.Message.ShouldBe("No Reading received for 60 s");
        alert.Status.ShouldBe(AlertStatus.Open);
        alert.RaisedAt.ShouldBe(_lastReceivedAt.AddSeconds(61));
        alert.CreatedAt.ShouldBe(_lastReceivedAt.AddSeconds(61));
        evaluation.State.ShouldBe(new RuleState(true, new ReadingId(_device, 1, 7)));
    }

    [Fact]
    public void A_Device_that_never_sent_a_Reading_counts_silence_from_the_attach()
    {
        var attachedAt = _lastReceivedAt;
        _time.Advance(TimeSpan.FromSeconds(59));
        Sweep(null, Tick(lastReceivedAt: null, attachedAt)).Raised.ShouldBeEmpty();

        _time.Advance(TimeSpan.FromSeconds(1));
        var evaluation = Sweep(null, Tick(lastReceivedAt: null, attachedAt));

        evaluation.Raised.ShouldHaveSingleItem();
        evaluation.State.ShouldBe(new RuleState(true, null));
    }

    [Fact]
    public void A_refitted_Device_counts_silence_from_the_attach_not_its_old_Reading()
    {
        _time.Advance(TimeSpan.FromHours(2));

        Sweep(null, Tick(_lastReceivedAt, attachedAt: _time.GetUtcNow().AddSeconds(-30))).Raised.ShouldBeEmpty();
    }

    [Fact]
    public void A_breached_rule_raises_nothing_more_so_a_manual_resolve_latches_it()
    {
        var breached = new RuleState(true, null);
        _time.Advance(TimeSpan.FromHours(1));

        var evaluation = Sweep(breached, Tick(_lastReceivedAt));

        evaluation.Raised.ShouldBeEmpty();
        evaluation.State.ShouldBe(breached);
    }

    [Fact]
    public void Nothing_is_raised_while_ingest_is_still_draining_the_broker_backlog()
    {
        _time.Advance(TimeSpan.FromHours(1));

        Sweep(null, Tick(_lastReceivedAt) with { CaughtUpAt = null }).Raised.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(59, false)]
    [InlineData(60, true)]
    public void After_a_restart_silence_counts_from_when_ingest_caught_up_with_the_backlog(int sinceCaughtUp, bool raises)
    {
        // The Device last reported an hour before the worker caught up: the rule still waits its seconds.
        var caughtUpAt = _lastReceivedAt.AddHours(1);
        _time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(sinceCaughtUp));

        Sweep(null, Tick(_lastReceivedAt) with { CaughtUpAt = caughtUpAt }).Raised.Count.ShouldBe(raises ? 1 : 0);
    }

    [Fact]
    public void A_sweep_leaves_other_kinds_of_rule_alone()
    {
        var overheat = new AlertRule
        {
            Id = Guid.NewGuid(),
            Name = "Engine overheat",
            Kind = AlertRuleKind.Threshold,
            AssetId = _asset,
            ThresholdSettings = new(Sensor.EngineTempC, ThresholdOperator.GreaterThan, 105, 5),
        };
        _time.Advance(TimeSpan.FromHours(1));

        var evaluation = RuleEvaluator.Sweep(overheat, _asset, null, Tick(_lastReceivedAt), _time);

        evaluation.Raised.ShouldBeEmpty();
        evaluation.State.ShouldBeNull();
    }

    [Fact]
    public void Any_stored_Reading_resolves_a_breached_rule_at_the_platform_time()
    {
        var breached = new RuleState(true, new ReadingId(_device, 1, 7));
        _time.Advance(TimeSpan.FromMinutes(5));

        var evaluation = Evaluate(breached, Reading(1, 8));

        evaluation.ResolveActiveAlertAt.ShouldBe(_time.GetUtcNow());
        evaluation.State.ShouldBe(new RuleState(false, new ReadingId(_device, 1, 7)));
        evaluation.Raised.ShouldBeEmpty();
    }

    [Fact]
    public void A_late_straggler_resolves_too_because_any_Reading_ends_the_silence()
    {
        var breached = new RuleState(true, new ReadingId(_device, 1, 7));

        Evaluate(breached, Reading(1, 3)).ResolveActiveAlertAt.ShouldNotBeNull();
    }

    [Fact]
    public void Readings_change_nothing_while_the_rule_is_clear_so_no_state_is_written_per_batch()
    {
        var clear = new RuleState(false, null);

        var evaluation = Evaluate(clear, Reading(1, 8));

        evaluation.State.ShouldBe(clear);
        evaluation.ResolveActiveAlertAt.ShouldBeNull();
        evaluation.Raised.ShouldBeEmpty();
        Evaluate(null, Reading(1, 8)).State.ShouldBeNull();
    }

    private static SweepTick Tick(DateTimeOffset? lastReceivedAt, DateTimeOffset? attachedAt = null) =>
        new(attachedAt ?? _attachedAt, lastReceivedAt, _caughtUpAt);

    private RuleEvaluation Sweep(RuleState? state, SweepTick tick) => RuleEvaluator.Sweep(_silent, _asset, state, tick, _time);

    private RuleEvaluation Evaluate(RuleState? state, params StoredReading[] readings) =>
        RuleEvaluator.Evaluate(_silent, _asset, state, readings, _time);

    private static StoredReading Reading(int boot, long sequenceNumber) =>
        new(_device, new Reading(boot, sequenceNumber, _lastReceivedAt, 51.5, -0.12, null, null, null, null, null));
}
