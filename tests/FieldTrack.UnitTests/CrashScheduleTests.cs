using FieldTrack.Simulator;
using FieldTrack.Simulator.Bench;

namespace FieldTrack.UnitTests;

public sealed class CrashScheduleTests
{
    private static readonly TimeSpan TenMinutes = TimeSpan.FromMinutes(10);

    [Fact]
    public void Ten_kills_in_ten_minutes_are_in_order_inside_the_run_and_far_enough_apart_to_recover()
    {
        var kills = CrashSchedule.Plan(10, TenMinutes, new Random(1));

        kills.Count.ShouldBe(10);
        kills.ShouldBe(kills.Order());
        kills[0].ShouldBeGreaterThan(TimeSpan.FromSeconds(30), "the run settles before the first kill");
        kills[^1].ShouldBeLessThan(TenMinutes - TimeSpan.FromSeconds(30), "the last kill leaves time to recover and be scored");
        for (var i = 1; i < kills.Count; i++)
        {
            (kills[i] - kills[i - 1]).ShouldBeGreaterThan(TimeSpan.FromSeconds(20), $"kill {i} follows the one before it");
        }
    }

    [Fact]
    public void Different_seeds_pick_different_moments()
    {
        CrashSchedule.Plan(10, TenMinutes, new Random(1)).ShouldNotBe(CrashSchedule.Plan(10, TenMinutes, new Random(2)));
    }

    [Fact]
    public void The_same_seed_picks_the_same_moments()
    {
        CrashSchedule.Plan(10, TenMinutes, new Random(7)).ShouldBe(CrashSchedule.Plan(10, TenMinutes, new Random(7)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Fewer_than_one_kill_is_refused_with_a_message_not_an_overflow(int kills)
    {
        Should.Throw<SimulatorException>(() => CrashSchedule.Plan(kills, TenMinutes, new Random(1))).Message.ShouldContain("--kills");
    }

    [Fact]
    public void A_run_too_short_for_its_kills_is_refused()
    {
        Should.Throw<SimulatorException>(() => CrashSchedule.Plan(10, TimeSpan.FromSeconds(60), new Random(1)));
    }
}
