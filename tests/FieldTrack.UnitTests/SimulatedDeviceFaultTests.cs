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
