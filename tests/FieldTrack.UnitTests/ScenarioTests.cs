using FieldTrack.Domain.Assets;
using FieldTrack.Simulator;

namespace FieldTrack.UnitTests;

public sealed class ScenarioTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("fieldtrack-scenario-").FullName;

    private static readonly FleetFile Fleet = FleetFile.Parse("""
        { "devices": [
          { "serial": "TRK-001", "assetType": "Truck", "sensors": ["engineTempC", "fuelPct"] },
          { "serial": "SUB-001", "assetType": "Sensor", "sensors": ["batteryV"] }
        ] }
        """, AppContext.BaseDirectory);

    private static Injection Window(InjectionKind kind, double start, double end, double amount = 0) =>
        new(kind, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), amount);

    [Fact]
    public void A_Scenario_lists_what_happens_to_which_Device_and_when()
    {
        var scenario = Scenario.Parse("""
            { "injections": [
              { "device": "TRK-001", "kind": "overheat", "atSeconds": 120, "durationSeconds": 600, "amount": 130 },
              { "device": "TRK-001", "kind": "fuelLeak", "atSeconds": 60, "durationSeconds": 300, "amount": 8 },
              { "device": "SUB-001", "kind": "powerOff", "atSeconds": 30, "durationSeconds": 45 },
              { "device": "SUB-001", "kind": "coverageGap", "atSeconds": 100, "durationSeconds": 90 },
              { "device": "TRK-001", "kind": "clockOffset", "atSeconds": 0, "durationSeconds": 1000, "amount": -20 },
              { "device": "SUB-001", "kind": "clockDrift", "atSeconds": 10, "durationSeconds": 500, "amount": 200 },
              { "device": "SUB-001", "kind": "clockJump", "atSeconds": 600, "amount": 900 }
            ] }
            """, Fleet);

        scenario.InjectionsFor("TRK-001").ShouldBe([
            Window(InjectionKind.Overheat, 120, 720, 130),
            Window(InjectionKind.FuelLeak, 60, 360, 8),
            Window(InjectionKind.ClockOffset, 0, 1000, -20),
        ]);
        scenario.InjectionsFor("SUB-001").ShouldBe([
            Window(InjectionKind.PowerOff, 30, 75),
            Window(InjectionKind.CoverageGap, 100, 190),
            Window(InjectionKind.ClockDrift, 10, 510, 200),
            new Injection(InjectionKind.ClockJump, TimeSpan.FromSeconds(600), TimeSpan.MaxValue, 900),
        ]);
    }

    [Fact]
    public void A_Fault_without_an_amount_gets_the_default_peak_or_leak_factor()
    {
        var scenario = Scenario.Parse("""
            { "injections": [
              { "device": "TRK-001", "kind": "overheat", "atSeconds": 0, "durationSeconds": 600 },
              { "device": "TRK-001", "kind": "fuelLeak", "atSeconds": 0, "durationSeconds": 600 }
            ] }
            """, Fleet);

        scenario.InjectionsFor("TRK-001").Select(injection => injection.Amount).ShouldBe([Injection.DefaultOverheatPeakC, Injection.DefaultFuelLeakFactor]);
    }

    [Fact]
    public void A_Device_the_Scenario_does_not_mention_has_nothing_scheduled()
    {
        Scenario.Parse("""{ "injections": [ { "device": "SUB-001", "kind": "powerOff", "atSeconds": 1, "durationSeconds": 1 } ] }""", Fleet)
            .InjectionsFor("TRK-001").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("not json", "not valid JSON")]
    [InlineData("""{ }""", "no injections")]
    [InlineData("""{ "injections": [] }""", "no injections")]
    [InlineData("""{ "injections": [ null ] }""", "no device")]
    [InlineData("""{ "injections": [ { "kind": "powerOff", "atSeconds": 0, "durationSeconds": 1 } ] }""", "no device")]
    [InlineData("""{ "injections": [ { "device": "NOPE", "kind": "powerOff", "atSeconds": 0, "durationSeconds": 1 } ] }""", "NOPE is not in the fleet")]
    [InlineData("""{ "injections": [ { "device": "SUB-001", "kind": "explode", "atSeconds": 0, "durationSeconds": 1 } ] }""", "unknown kind 'explode'")]
    [InlineData("""{ "injections": [ { "device": "SUB-001", "kind": "3", "atSeconds": 0, "durationSeconds": 1 } ] }""", "unknown kind '3'")]
    [InlineData("""{ "injections": [ { "device": "SUB-001", "kind": "powerOff", "durationSeconds": 1 } ] }""", "atSeconds")]
    [InlineData("""{ "injections": [ { "device": "SUB-001", "kind": "powerOff", "atSeconds": -1, "durationSeconds": 1 } ] }""", "atSeconds")]
    [InlineData("""{ "injections": [ { "device": "SUB-001", "kind": "powerOff", "atSeconds": 5000000, "durationSeconds": 1 } ] }""", "atSeconds")]
    [InlineData("""{ "injections": [ { "device": "SUB-001", "kind": "powerOff", "atSeconds": 0 } ] }""", "durationSeconds")]
    [InlineData("""{ "injections": [ { "device": "SUB-001", "kind": "powerOff", "atSeconds": 0, "durationSeconds": 0 } ] }""", "durationSeconds")]
    [InlineData("""{ "injections": [ { "device": "SUB-001", "kind": "powerOff", "atSeconds": 0, "durationSeconds": 1e-9 } ] }""", "rounds to no time")]
    [InlineData("""{ "injections": [ { "device": "SUB-001", "kind": "clockJump", "atSeconds": 0, "durationSecond": 5, "amount": 9 } ] }""", "not valid JSON")]
    [InlineData("""{ "injections": [], "note": "x" }""", "not valid JSON")]
    [InlineData("""{ "injections": [ { "device": "TRK-001", "kind": "overheat", "atSeconds": 0, "durationSeconds": 5, "amount": 80 } ] }""", "above the normal 85")]
    [InlineData("""{ "injections": [ { "device": "TRK-001", "kind": "fuelLeak", "atSeconds": 0, "durationSeconds": 5, "amount": 1 } ] }""", "above 1")]
    [InlineData("""{ "injections": [ { "device": "SUB-001", "kind": "clockOffset", "atSeconds": 0, "durationSeconds": 5 } ] }""", "needs an amount")]
    [InlineData("""{ "injections": [ { "device": "SUB-001", "kind": "clockJump", "atSeconds": 0 } ] }""", "needs an amount")]
    [InlineData("""{ "injections": [ { "device": "SUB-001", "kind": "overheat", "atSeconds": 0, "durationSeconds": 5 } ] }""", "does not carry engineTempC")]
    [InlineData("""{ "injections": [ { "device": "SUB-001", "kind": "fuelLeak", "atSeconds": 0, "durationSeconds": 5 } ] }""", "does not carry fuelPct")]
    [InlineData("""{ "injections": [ { "device": "SUB-001", "kind": "powerOff", "atSeconds": 0, "durationSeconds": 10 }, { "device": "SUB-001", "kind": "coverageGap", "atSeconds": 5, "durationSeconds": 10 } ] }""", "overlaps")]
    public void A_Scenario_that_breaks_a_rule_is_refused_with_the_reason(string json, string reason)
    {
        Should.Throw<SimulatorException>(() => Scenario.Parse(json, Fleet)).Message.ShouldContain(reason);
    }

    private static IEnumerable<(string Serial, Injection Injection)> DemoInjections()
    {
        var fleet = FleetFile.Load(Path.Combine(AppContext.BaseDirectory, "fleets", "demo.json"));
        var scenario = Scenario.Load(Path.Combine(AppContext.BaseDirectory, "fleets", "demo-scenario.json"), fleet);
        return fleet.Devices.SelectMany(device => scenario.InjectionsFor(device.Serial).Select(injection => (device.Serial, injection)));
    }

    [Fact]
    public void The_demo_Scenario_silences_one_Device_by_a_Power_off_and_one_by_a_Coverage_gap_for_longer_than_Asset_Status_waits_to_show_Offline()
    {
        var silences = DemoInjections()
            .Where(demo => demo.Injection.Kind is InjectionKind.PowerOff or InjectionKind.CoverageGap)
            .Where(demo => demo.Injection.End - demo.Injection.Start > AssetStatuses.OfflineAfter)
            .ToList();

        silences.Select(demo => demo.Injection.Kind).ShouldBe([InjectionKind.PowerOff, InjectionKind.CoverageGap], ignoreOrder: true);
        silences.Select(demo => demo.Serial).Distinct().Count().ShouldBe(2);
    }

    [Fact]
    public void The_demo_Scenario_overheats_a_Device_wholly_inside_its_Coverage_gap_so_the_Alert_arrives_with_the_backlog()
    {
        var injections = DemoInjections().ToList();

        injections.ShouldContain(overheat => overheat.Injection.Kind == InjectionKind.Overheat && injections.Any(gap =>
            gap.Serial == overheat.Serial && gap.Injection.Kind == InjectionKind.CoverageGap
            && gap.Injection.Start <= overheat.Injection.Start && overheat.Injection.End <= gap.Injection.End));
    }

    [Fact]
    public void A_Scenario_file_is_read_from_disk()
    {
        var path = Path.Combine(_directory, "scenario.json");
        File.WriteAllText(path, """{ "injections": [ { "device": "SUB-001", "kind": "powerOff", "atSeconds": 1, "durationSeconds": 2 } ] }""");

        Scenario.Load(path, Fleet).InjectionsFor("SUB-001").ShouldBe([Window(InjectionKind.PowerOff, 1, 3)]);
        Should.Throw<SimulatorException>(() => Scenario.Load(Path.Combine(_directory, "missing.json"), Fleet)).Message.ShouldContain("missing.json");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
