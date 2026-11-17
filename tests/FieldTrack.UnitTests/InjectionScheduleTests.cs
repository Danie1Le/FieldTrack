using FieldTrack.Simulator;

namespace FieldTrack.UnitTests;

public sealed class InjectionScheduleTests
{
    private static Injection Window(InjectionKind kind, double startSeconds, double endSeconds) =>
        new(kind, TimeSpan.FromSeconds(startSeconds), TimeSpan.FromSeconds(endSeconds), 0);

    [Fact]
    public void An_Injection_is_in_force_from_its_start_up_to_but_not_including_its_end()
    {
        var schedule = new InjectionSchedule();
        schedule.TryAdd(Window(InjectionKind.PowerOff, 10, 20)).ShouldBeTrue();

        schedule.Active(InjectionLane.Connection, TimeSpan.FromSeconds(9.9)).ShouldBeNull();
        schedule.Active(InjectionLane.Connection, TimeSpan.FromSeconds(10)).ShouldNotBeNull();
        schedule.Active(InjectionLane.Connection, TimeSpan.FromSeconds(19.9)).ShouldNotBeNull();
        schedule.Active(InjectionLane.Connection, TimeSpan.FromSeconds(20)).ShouldBeNull();
    }

    [Theory]
    [InlineData("PowerOff", "CoverageGap", 15, 25)]
    [InlineData("PowerOff", "PowerOff", 0, 100)]
    [InlineData("ClockOffset", "ClockJump", 19, 30)]
    [InlineData("Overheat", "Overheat", 10, 11)]
    public void Two_Injections_of_one_lane_cannot_overlap(string first, string second, double start, double end)
    {
        var schedule = new InjectionSchedule();
        schedule.TryAdd(Window(Enum.Parse<InjectionKind>(first), 10, 20)).ShouldBeTrue();

        schedule.TryAdd(Window(Enum.Parse<InjectionKind>(second), start, end)).ShouldBeFalse();

        schedule.All.Count.ShouldBe(1);
    }

    [Fact]
    public void Injections_that_touch_or_sit_in_different_lanes_are_allowed()
    {
        var schedule = new InjectionSchedule();
        schedule.TryAdd(Window(InjectionKind.PowerOff, 10, 20)).ShouldBeTrue();

        schedule.TryAdd(Window(InjectionKind.CoverageGap, 20, 30)).ShouldBeTrue("a window ends where the next one starts");
        schedule.TryAdd(Window(InjectionKind.Overheat, 10, 30)).ShouldBeTrue();
        schedule.TryAdd(Window(InjectionKind.FuelLeak, 10, 30)).ShouldBeTrue();
        schedule.TryAdd(Window(InjectionKind.ClockDrift, 10, 30)).ShouldBeTrue();
    }
}
