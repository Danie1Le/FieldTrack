using FieldTrack.Simulator;

namespace FieldTrack.UnitTests;

public sealed class RandomInjectionsTests
{
    private static readonly Sensor[] AllSensors = [.. Enum.GetValues<Sensor>()];

    // One event per second per kind: rate x 1 s >= 1 makes every draw certain.
    private static readonly RandomRates Certain = new(FaultsPerHour: 3600, PowerOffsPerHour: 3600, CoverageGapsPerHour: 3600);

    private static TimeSpan Seconds(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void Nothing_is_drawn_at_rate_0()
    {
        var random = new RandomInjections(RandomRates.None, AllSensors, new Random(1));

        random.Draw(Seconds(0), Seconds(3600)).ShouldBeEmpty();
    }

    [Fact]
    public void A_certain_draw_starts_a_Fault_a_Power_off_and_a_Coverage_gap_now_with_the_standard_durations()
    {
        var random = new RandomInjections(Certain, AllSensors, new Random(1));

        var drawn = random.Draw(Seconds(10), Seconds(11));

        drawn.Select(injection => (injection.Kind, injection.Start, injection.End - injection.Start)).ShouldBe(
        [
            (drawn[0].Kind, Seconds(11), Seconds(600)),
            (InjectionKind.PowerOff, Seconds(11), Seconds(60)),
            (InjectionKind.CoverageGap, Seconds(11), Seconds(120)),
        ]);
        drawn[0].Kind.ShouldBeOneOf(InjectionKind.Overheat, InjectionKind.FuelLeak);
        drawn[0].Amount.ShouldBe(drawn[0].Kind == InjectionKind.Overheat ? Injection.DefaultOverheatPeakC : Injection.DefaultFuelLeakFactor);
    }

    [Theory]
    [InlineData("EngineTempC", "Overheat")]
    [InlineData("FuelPct", "FuelLeak")]
    public void A_Fault_is_drawn_only_for_a_sensor_the_Device_carries(string sensor, string fault)
    {
        var random = new RandomInjections(new RandomRates(3600, 0, 0), [Enum.Parse<Sensor>(sensor), Sensor.BatteryV], new Random(1));

        Enumerable.Range(0, 20).SelectMany(second => random.Draw(Seconds(second), Seconds(second + 1)))
            .ShouldAllBe(injection => injection.Kind == Enum.Parse<InjectionKind>(fault));
    }

    [Fact]
    public void A_Device_with_neither_Fault_sensor_draws_no_Faults()
    {
        var random = new RandomInjections(new RandomRates(3600, 0, 0), [Sensor.BatteryV], new Random(1));

        random.Draw(Seconds(0), Seconds(100)).ShouldBeEmpty();
    }

    [Fact]
    public void The_draw_rate_follows_the_rate_per_Device_hour()
    {
        // 6 Power offs per Device-hour, drawn every minute for 1000 hours: about 6000, each draw independent.
        var random = new RandomInjections(new RandomRates(0, 6, 0), AllSensors, new Random(42));

        var count = Enumerable.Range(0, 60 * 1000).Sum(minute => random.Draw(Seconds(minute * 60), Seconds((minute + 1) * 60)).Count);

        count.ShouldBeInRange(5700, 6300);
    }

    [Fact]
    public void The_same_seed_draws_the_same_injections()
    {
        var first = new RandomInjections(new RandomRates(60, 60, 60), AllSensors, new Random(7));
        var second = new RandomInjections(new RandomRates(60, 60, 60), AllSensors, new Random(7));

        Enumerable.Range(0, 600).SelectMany(s => first.Draw(Seconds(s), Seconds(s + 1)))
            .ShouldBe(Enumerable.Range(0, 600).SelectMany(s => second.Draw(Seconds(s), Seconds(s + 1))));
    }

    [Fact]
    public void A_schedule_adds_what_is_drawn_and_skips_a_draw_that_clashes_with_one_in_force()
    {
        var schedule = new InjectionSchedule(new RandomInjections(new RandomRates(0, 3600, 0), AllSensors, new Random(1)));
        schedule.TryAdd(new Injection(InjectionKind.CoverageGap, Seconds(100), Seconds(200), 0)).ShouldBeTrue();

        for (var second = 1; second <= 120; second++)
        {
            schedule.Advance(Seconds(second));
        }

        // The Power off at 1 s lasts to 61 s; every draw after that would run into the gap at 100 s or into the gap itself.
        schedule.All.Select(injection => (injection.Kind, injection.Start)).ShouldBe(
            [(InjectionKind.CoverageGap, Seconds(100)), (InjectionKind.PowerOff, Seconds(1))]);
    }
}
