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
    public void Fewer_than_three_samples_say_nothing_about_a_trend()
    {
        Should.Throw<ArgumentException>(() => BenchStats.IsFlat([1, 2], tolerance: 1_000));
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
