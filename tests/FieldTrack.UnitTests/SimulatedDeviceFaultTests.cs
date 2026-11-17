using System.Text.Json;
using FieldTrack.Domain.Assets;
using FieldTrack.Simulator;

namespace FieldTrack.UnitTests;

public sealed class SimulatedDeviceFaultTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private static SimulatedDevice Device(Injection[] injections, params Sensor[] sensors)
    {
        var schedule = new InjectionSchedule();
        foreach (var injection in injections)
        {
            schedule.TryAdd(injection).ShouldBeTrue();
        }

        return new SimulatedDevice(
            new FleetDevice("TRK-001", "Haul truck 1", AssetType.Truck, null, null, 30, sensors),
            boot: 1,
            new FixedMotion(new Position(45.5, -122.6)),
            schedule);
    }

    private static Injection Window(InjectionKind kind, double startSeconds, double endSeconds, double amount = 0) =>
        new(kind, TimeSpan.FromSeconds(startSeconds), TimeSpan.FromSeconds(endSeconds), amount);

    // One tick of a Device whose broker works: the Device takes a Reading and the broker acknowledges what it holds.
    private static JsonElement Tick(SimulatedDevice device, double seconds)
    {
        device.Step(Now.AddSeconds(seconds), TimeSpan.FromSeconds(seconds));
        var payload = device.NextToSend!;
        device.Published();
        return JsonDocument.Parse(payload).RootElement;
    }

    [Fact]
    public void A_Power_off_sends_and_keeps_nothing_then_the_Device_starts_a_new_Boot_at_Sequence_number_0()
    {
        var device = Device([Window(InjectionKind.PowerOff, 100, 200)], Sensor.FuelPct);
        Tick(device, 80).GetProperty("seq").GetInt64().ShouldBe(0);
        device.Step(Now.AddSeconds(90), TimeSpan.FromSeconds(90));
        device.NextToSend.ShouldNotBeNull("a Reading taken just before the power was cut and not yet acknowledged");

        foreach (var seconds in new[] { 100, 150, 199.9 })
        {
            device.Step(Now.AddSeconds(seconds), TimeSpan.FromSeconds(seconds));
            device.NextToSend.ShouldBeNull($"{seconds} s: the Device is off");
        }

        var back = Tick(device, 200);

        back.GetProperty("boot").GetInt32().ShouldBe(2);
        back.GetProperty("seq").GetInt64().ShouldBe(0);
        device.Boot.ShouldBe(2);
        device.Ranges.ShouldBe([new PublishedRange(1, 0, 0), new PublishedRange(2, 0, 0)]);
    }

    [Theory]
    [InlineData("ClockOffset", 99, 0)]
    [InlineData("ClockOffset", 100, -30)]
    [InlineData("ClockOffset", 199, -30)]
    [InlineData("ClockOffset", 200, 0)]
    [InlineData("ClockDrift", 100, 0)]
    [InlineData("ClockDrift", 150, 50)]
    [InlineData("ClockDrift", 199, 99)]
    [InlineData("ClockDrift", 200, 0)]
    [InlineData("ClockJump", 99, 0)]
    [InlineData("ClockJump", 100, 600)]
    [InlineData("ClockJump", 100000, 600)]
    public void A_clock_fault_moves_the_Device_time_off_the_real_time_only_while_it_lasts(string kind, double seconds, double errorSeconds)
    {
        // Offset -30 s; drift 1,000,000 ppm (one second gained per second); jump 600 s that never ends.
        var amount = kind switch { "ClockOffset" => -30, "ClockDrift" => 1_000_000, _ => 600 };
        var end = kind == "ClockJump" ? TimeSpan.MaxValue : TimeSpan.FromSeconds(200);
        var device = Device([new Injection(Enum.Parse<InjectionKind>(kind), TimeSpan.FromSeconds(100), end, amount)], Sensor.FuelPct);

        var deviceTime = Tick(device, seconds).GetProperty("ts").GetInt64();

        deviceTime.ShouldBe(Now.AddSeconds(seconds + errorSeconds).ToUnixTimeMilliseconds());
    }

    [Fact]
    public void A_Coverage_gap_keeps_the_Readings_and_sends_them_late_in_order_in_the_same_Boot()
    {
        var device = Device([Window(InjectionKind.CoverageGap, 100, 200)], Sensor.FuelPct);
        Tick(device, 50).GetProperty("seq").GetInt64().ShouldBe(0);

        foreach (var seconds in new[] { 100, 150 })
        {
            device.Step(Now.AddSeconds(seconds), TimeSpan.FromSeconds(seconds));
            device.NextToSend.ShouldBeNull($"{seconds} s: no coverage, so the Reading stays on the Device");
        }

        device.Step(Now.AddSeconds(200), TimeSpan.FromSeconds(200));
        var late = new List<JsonElement>();
        while (device.NextToSend is { } payload)
        {
            late.Add(JsonDocument.Parse(payload).RootElement);
            device.Published();
        }

        late.Select(reading => reading.GetProperty("seq").GetInt64()).ShouldBe([1, 2, 3]);
        late.Select(reading => reading.GetProperty("boot").GetInt32()).ShouldAllBe(boot => boot == 1);
        long[] capturedAt = [Now.AddSeconds(100).ToUnixTimeMilliseconds(), Now.AddSeconds(150).ToUnixTimeMilliseconds(), Now.AddSeconds(200).ToUnixTimeMilliseconds()];
        late.Select(reading => reading.GetProperty("ts").GetInt64()).ShouldBe(capturedAt);
        device.Ranges.ShouldBe([new PublishedRange(1, 0, 3)]);
    }

    [Fact]
    public void A_Power_off_in_the_middle_of_a_Coverage_gap_loses_what_was_kept()
    {
        var device = Device([Window(InjectionKind.CoverageGap, 100, 150), Window(InjectionKind.PowerOff, 150, 200)], Sensor.FuelPct);
        Tick(device, 50);
        device.Step(Now.AddSeconds(120), TimeSpan.FromSeconds(120));

        device.Step(Now.AddSeconds(160), TimeSpan.FromSeconds(160));
        var back = Tick(device, 200);

        device.Ranges.ShouldBe([new PublishedRange(1, 0, 0), new PublishedRange(2, 0, 0)]);
        back.GetProperty("seq").GetInt64().ShouldBe(0);
    }

    [Fact]
    public void A_Device_lets_random_injection_draw_at_every_tick()
    {
        var schedule = new InjectionSchedule(new RandomInjections(new RandomRates(0, PowerOffsPerHour: 3600, 0), [], new Random(1)));
        var device = new SimulatedDevice(
            new FleetDevice("TRK-001", "Haul truck 1", AssetType.Truck, null, null, 30, []), 1, new FixedMotion(new Position(1, 2)), schedule);

        device.Step(Now.AddSeconds(1), TimeSpan.FromSeconds(1));

        device.NextToSend.ShouldBeNull("the drawn Power off starts at this tick");
        schedule.All.ShouldHaveSingleItem().Kind.ShouldBe(InjectionKind.PowerOff);
    }

    [Fact]
    public void A_Power_off_between_two_ticks_still_starts_a_new_Boot_taken_from_the_Boot_counter()
    {
        var schedule = new InjectionSchedule();
        schedule.TryAdd(Window(InjectionKind.PowerOff, 10, 20));
        var device = new SimulatedDevice(
            new FleetDevice("TRK-001", "Haul truck 1", AssetType.Truck, null, null, 30, []), 4, new FixedMotion(new Position(1, 2)), schedule, raiseBoot: () => 9);

        Tick(device, 5);
        Tick(device, 20).GetProperty("boot").GetInt32().ShouldBe(9);
    }

    [Theory]
    [InlineData(0, 90)]
    [InlineData(540, 85.5)]
    [InlineData(630, 82.5)]
    [InlineData(1170, 37.5)]
    [InlineData(1200, 35)]
    [InlineData(1500, 32.5)]
    public void A_fuel_leak_Fault_multiplies_the_drain_only_while_it_lasts(double seconds, double expected)
    {
        // Normal drain is 0.5 % a minute; the leak (10 x) runs from 10 to 20 minutes, 5 % a minute.
        var device = Device([Window(InjectionKind.FuelLeak, 600, 1200, amount: 10)], Sensor.FuelPct);

        JsonElement last = default;
        for (var tick = 0.0; tick <= seconds; tick += 30)
        {
            last = Tick(device, tick);
        }

        last.GetProperty("fuelPct").GetDouble().ShouldBe(expected, tolerance: 0.01);
    }

    [Theory]
    [InlineData(0, 85)]
    [InlineData(59, 85)]
    [InlineData(60, 85)]
    [InlineData(180, 102.5)]
    [InlineData(300, 120)]
    [InlineData(400, 120)]
    [InlineData(480, 120)]
    [InlineData(570, 102.5)]
    [InlineData(659, 85.19)]
    [InlineData(660, 85)]
    [InlineData(900, 85)]
    public void An_overheat_Fault_ramps_the_engine_to_its_peak_holds_and_cools(double seconds, double expected)
    {
        // 600 s window: 40% ramp, 30% hold, 30% cool.
        var device = Device([Window(InjectionKind.Overheat, 60, 660, amount: 120)], Sensor.EngineTempC, Sensor.FuelPct);

        Tick(device, seconds).GetProperty("engineTempC").GetDouble().ShouldBe(expected, tolerance: 0.01);
    }
}
