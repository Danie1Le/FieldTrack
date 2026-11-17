using FieldTrack.Simulator.Bench;

namespace FieldTrack.UnitTests;

public sealed class BenchStatsTests
{
    [Fact]
    public void A_backlog_that_keeps_growing_is_not_flat()
    {
        double[] backlog = [.. Enumerable.Range(0, 60).Select(second => second * 100.0)];

        BenchStats.IsFlat(backlog, tolerance: 1_000).ShouldBeFalse();
    }

    [Fact]
    public void A_backlog_that_wobbles_around_one_level_is_flat()
    {
        double[] backlog = [.. Enumerable.Range(0, 60).Select(second => 200.0 + (second % 5 * 40))];

        BenchStats.IsFlat(backlog, tolerance: 1_000).ShouldBeTrue();
    }

    [Fact]
    public void A_burst_in_the_middle_that_drains_again_is_flat()
    {
        double[] backlog = [.. Enumerable.Range(0, 90).Select(second => second is >= 35 and < 55 ? 8_000.0 : 200.0)];

        BenchStats.IsFlat(backlog, tolerance: 1_000).ShouldBeTrue();
    }

    [Fact]
    public void A_backlog_that_starts_high_and_drains_is_flat()
    {
        double[] backlog = [.. Enumerable.Range(0, 60).Select(second => second < 20 ? 5_000.0 : 100.0)];

        BenchStats.IsFlat(backlog, tolerance: 1_000).ShouldBeTrue();
    }

    [Fact]
    public void A_backlog_that_starts_climbing_in_the_last_tenth_is_not_flat()
    {
        // The last third's median is still low; only the last few samples show the climb.
        double[] backlog = [.. Enumerable.Range(0, 90).Select(second => second < 80 ? 200.0 : 200.0 + ((second - 79) * 2_000))];

        BenchStats.IsFlat(backlog, tolerance: 1_000).ShouldBeFalse();
    }

    [Fact]
    public void Fewer_than_three_samples_say_nothing_about_a_trend()
    {
        Should.Throw<ArgumentException>(() => BenchStats.IsFlat([1, 2], tolerance: 1_000));
    }

    [Theory]
    [InlineData(2_000, true)]
    [InlineData(1_961, true)]
    [InlineData(1_959, false)]
    [InlineData(1_000, false)]
    public void A_run_holds_its_rate_within_two_percent(double achieved, bool holds)
    {
        ThroughputBench.HoldsRate(achieved, target: 2_000).ShouldBe(holds);
    }

    [Fact]
    public void Clocks_that_moved_more_than_a_few_milliseconds_during_a_run_are_flagged_and_latency_uses_the_mean()
    {
        var steady = new ClockOffsets(TimeSpan.FromMilliseconds(12), TimeSpan.FromMilliseconds(18));
        var moved = new ClockOffsets(TimeSpan.FromMilliseconds(12), TimeSpan.FromMilliseconds(112));

        steady.Drifted.ShouldBeFalse();
        steady.Mean.ShouldBe(TimeSpan.FromMilliseconds(15));
        moved.Drifted.ShouldBeTrue();
        moved.Drift.ShouldBe(TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public void A_percentile_interpolates_between_neighbours()
    {
        BenchStats.Percentile([1, 2, 3, 4, 5], 0.95).ShouldBe(4.8, 1e-9);
        BenchStats.Percentile([7], 0.99).ShouldBe(7);
    }

    [Theory]
    [InlineData(new double[] { 5 }, 5)]
    [InlineData(new double[] { 3, 1, 2 }, 2)]
    [InlineData(new double[] { 4, 1, 3, 2 }, 2.5)]
    public void Median_is_the_middle_value_or_the_mean_of_the_middle_two(double[] values, double expected)
    {
        BenchStats.Median(values).ShouldBe(expected);
    }
}
