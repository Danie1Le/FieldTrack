using FieldTrack.Simulator.Bench;

namespace FieldTrack.UnitTests;

public sealed class AlertBenchScoringTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Rule = TimeSpan.FromSeconds(20);

    [Fact]
    public void Alert_latency_splits_publish_to_push_at_CreatedAt_after_correcting_the_database_clock()
    {
        // The database clock runs 300 ms ahead of this machine's: CreatedAt 450 ms "late" on its clock is 150 ms after the publish on ours.
        var timing = new AlertTiming(PublishedAt: T0, CreatedAt: T0.AddMilliseconds(450), ReceivedAt: T0.AddMilliseconds(220));

        var latency = AlertLatency.Of([timing], TimeSpan.FromMilliseconds(300));

        latency!.Total.P95Ms.ShouldBe(220, 0.001);
        latency.Server.P95Ms.ShouldBe(150, 0.001);
        latency.Push.P95Ms.ShouldBe(70, 0.001);
    }

    [Fact]
    public void Alert_latency_of_no_alerts_is_null() => AlertLatency.Of([], TimeSpan.Zero).ShouldBeNull();

    private static Dropout Window(int startSeconds, int lengthSeconds, int lastReceivedSeconds) =>
        new("A", T0.AddSeconds(startSeconds), T0.AddSeconds(startSeconds + lengthSeconds), T0.AddSeconds(lastReceivedSeconds));

    [Fact]
    public void A_long_dropout_is_detected_when_its_Alert_is_created_within_the_rule_seconds_plus_two_of_the_last_Received_Reading()
    {
        var score = OfflineScore.Of([Window(100, 60, 99)], [("A", T0.AddSeconds(99 + 20 + 2))], Rule);

        score.ShouldBe(new OfflineScore(LongDropouts: 1, Detected: 1, Late: 0, Missed: 0, FalseAlarms: 0));
    }

    [Fact]
    public void An_Alert_one_millisecond_past_the_limit_is_late_not_detected()
    {
        var score = OfflineScore.Of([Window(100, 60, 99)], [("A", T0.AddSeconds(99 + 20 + 2).AddMilliseconds(1))], Rule);

        score.ShouldBe(new OfflineScore(1, 0, 1, 0, 0));
    }

    [Fact]
    public void A_long_dropout_without_an_Alert_is_missed() =>
        OfflineScore.Of([Window(100, 60, 99)], [], Rule).ShouldBe(new OfflineScore(1, 0, 0, 1, 0));

    [Fact]
    public void An_Alert_during_a_short_dropout_is_a_false_alarm_and_the_short_dropout_is_not_counted()
    {
        var score = OfflineScore.Of([Window(100, 10, 99)], [("A", T0.AddSeconds(105))], Rule);

        score.ShouldBe(new OfflineScore(0, 0, 0, 0, 1));
    }

    [Fact]
    public void An_Alert_in_the_band_between_the_rule_seconds_and_a_long_dropout_is_neither_owed_nor_a_false_alarm()
    {
        // 22 s of silence against a 20 s rule: the rule rightly fires, but the dropout is under 25 s so it is not a long one.
        var score = OfflineScore.Of([Window(100, 22, 99)], [("A", T0.AddSeconds(121))], Rule);

        score.ShouldBe(new OfflineScore(0, 0, 0, 0, 0));
    }

    [Fact]
    public void A_Fault_window_is_caught_when_an_Alert_of_its_Device_was_open_during_it()
    {
        var faults = new Dictionary<string, IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)>>
        {
            ["A"] = [(T0.AddSeconds(10), T0.AddSeconds(20)), (T0.AddSeconds(50), T0.AddSeconds(60))],
            ["B"] = [(T0.AddSeconds(10), T0.AddSeconds(20))],
        };

        HysteresisScore.FaultWindowsCaught(
            [("A", T0.AddSeconds(12), T0.AddSeconds(14)), ("B", T0.AddSeconds(5), null), ("A", T0.AddSeconds(60), null), ("A", T0.AddSeconds(30), T0.AddSeconds(50))],
            faults)
            .ShouldBe(2, "A's first window by an Alert open inside it, B's by an Alert raised before it and still open; A's second window by none: one resolved at its start, one raised at its end");
    }

    [Theory]
    [InlineData(10, 54, 44, 10, 10, true)]
    [InlineData(0, 54, 0, 0, 0, false)]
    [InlineData(10, 0, 0, 10, 10, false)]
    [InlineData(10, 54, 60, 10, 10, false)]
    [InlineData(10, 54, 0, 10, 0, false)]
    public void The_A_B_passes_only_with_Fault_windows_noise_false_Alerts_and_no_real_Fault_lost(
        int windows, int falseWithout, int falseWith, int caughtWithout, int caughtWith, bool expected) =>
        HysteresisScore.Passes(windows, falseWithout, falseWith, caughtWithout, caughtWith).ShouldBe(expected);

    [Fact]
    public void An_Alert_of_another_Device_does_not_match_a_dropout()
    {
        var score = OfflineScore.Of([Window(100, 60, 99)], [("B", T0.AddSeconds(121))], Rule);

        score.ShouldBe(new OfflineScore(1, 0, 0, 1, 1));
    }

    [Fact]
    public void One_Alert_matches_one_dropout_so_a_second_Alert_in_the_same_dropout_is_false()
    {
        var score = OfflineScore.Of([Window(100, 60, 99)], [("A", T0.AddSeconds(121)), ("A", T0.AddSeconds(130))], Rule);

        score.ShouldBe(new OfflineScore(1, 1, 0, 0, 1));
    }

    [Fact]
    public void False_Alerts_are_Threshold_Alerts_raised_outside_every_Fault_window_of_their_Device()
    {
        var faults = new Dictionary<string, IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)>>
        {
            ["A"] = [(T0.AddSeconds(10), T0.AddSeconds(20))],
        };

        HysteresisScore.FalseAlerts(
            [("A", T0.AddSeconds(5)), ("A", T0.AddSeconds(10)), ("A", T0.AddSeconds(19)), ("A", T0.AddSeconds(20)), ("B", T0.AddSeconds(15))], faults)
            .ShouldBe(3, "[start, end): 5 s, 20 s and every Alert of a Device without a Fault window are false");
    }

    [Theory]
    [InlineData(100, 25, 75.0)]
    [InlineData(10, 10, 0.0)]
    [InlineData(10, 12, -20.0)]
    public void Reduction_is_the_percentage_of_false_Alerts_the_Hysteresis_removed(int without, int with, double expected) =>
        HysteresisScore.ReductionPercent(without, with).ShouldBe(expected);

    [Fact]
    public void Reduction_is_undefined_when_there_was_nothing_to_remove() => HysteresisScore.ReductionPercent(0, 0).ShouldBeNull();
}
