using System.Text.Json;
using FieldTrack.Domain.Assets;
using FieldTrack.Simulator;

namespace FieldTrack.UnitTests;

public sealed class RunManifestTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Directory.CreateTempSubdirectory("fieldtrack-manifest-").FullName;

    private static SimulatedDevice Device(string serial, int boot) =>
        new(new FleetDevice(serial, serial, AssetType.Truck, null, null, 30, []), boot, new FixedMotion(new Position(1, 2)));

    [Fact]
    public void Each_Device_lists_the_Boot_and_the_Sequence_numbers_it_published()
    {
        var busy = Device("TRK-001", boot: 3);
        for (var i = 0; i < 5; i++)
        {
            busy.Step(Start, TimeSpan.Zero);
            busy.Published();
        }

        var quiet = Device("GEN-001", boot: 1);
        quiet.Step(Start, TimeSpan.Zero);

        var manifest = RunManifest.Of(Start, Start.AddMinutes(1), ratePerSecond: 20, [busy, quiet]);

        manifest.Devices.Single(device => device.Serial == "TRK-001").Ranges.ShouldBe([new PublishedRange(3, 0, 4)]);
        manifest.Devices.Single(device => device.Serial == "GEN-001").Ranges.ShouldBeEmpty();
    }

    [Fact]
    public void Each_Device_lists_the_windows_of_what_was_injected_with_times_cut_to_the_run()
    {
        var schedule = new InjectionSchedule();
        schedule.TryAdd(new Injection(InjectionKind.PowerOff, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), 0));
        schedule.TryAdd(new Injection(InjectionKind.Overheat, TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(650), 120));
        schedule.TryAdd(new Injection(InjectionKind.ClockJump, TimeSpan.FromSeconds(55), TimeSpan.MaxValue, 900));
        schedule.TryAdd(new Injection(InjectionKind.CoverageGap, TimeSpan.FromSeconds(1000), TimeSpan.FromSeconds(1100), 0));
        var injected = new SimulatedDevice(
            new FleetDevice("TRK-001", "TRK-001", AssetType.Truck, null, null, 30, []), 1, new FixedMotion(new Position(1, 2)), schedule);

        var manifest = RunManifest.Of(Start, Start.AddSeconds(60), ratePerSecond: 20, [injected, Device("GEN-001", boot: 1)]);

        manifest.Devices[0].Windows.ShouldBe(
        [
            new ManifestWindow(InjectionKind.PowerOff, Start.AddSeconds(10), Start.AddSeconds(20), 0),
            new ManifestWindow(InjectionKind.Overheat, Start.AddSeconds(50), Start.AddSeconds(60), 120),
            new ManifestWindow(InjectionKind.ClockJump, Start.AddSeconds(55), Start.AddSeconds(60), 900),
        ]);
        manifest.Devices[1].Windows.ShouldBeEmpty();
    }

    [Fact]
    public void The_file_is_JSON_a_benchmark_can_read_back()
    {
        var schedule = new InjectionSchedule();
        schedule.TryAdd(new Injection(InjectionKind.CoverageGap, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), 0));
        var device = new SimulatedDevice(
            new FleetDevice("TRK-001", "TRK-001", AssetType.Truck, null, null, 30, []), 2, new FixedMotion(new Position(1, 2)), schedule);
        device.Step(Start, TimeSpan.Zero);
        device.Published();
        var path = Path.Combine(_directory, "nested", "manifest.json");

        RunManifest.Of(Start, Start.AddSeconds(30), 20, [device]).Write(path);

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        root.GetProperty("startedAt").GetDateTimeOffset().ShouldBe(Start);
        root.GetProperty("endedAt").GetDateTimeOffset().ShouldBe(Start.AddSeconds(30));
        root.GetProperty("ratePerSecond").GetDouble().ShouldBe(20);
        var range = root.GetProperty("devices")[0].GetProperty("ranges")[0];
        range.GetProperty("boot").GetInt32().ShouldBe(2);
        range.GetProperty("firstSequence").GetInt64().ShouldBe(0);
        range.GetProperty("lastSequence").GetInt64().ShouldBe(0);
        var window = root.GetProperty("devices")[0].GetProperty("windows")[0];
        window.GetProperty("kind").GetString().ShouldBe("coverageGap");
        window.GetProperty("start").GetDateTimeOffset().ShouldBe(Start.AddSeconds(5));
        window.GetProperty("end").GetDateTimeOffset().ShouldBe(Start.AddSeconds(15));
    }

    [Fact]
    public void A_maximum_rate_run_has_no_target_rate()
    {
        var path = Path.Combine(_directory, "max.json");

        RunManifest.Of(Start, Start.AddSeconds(30), ratePerSecond: null, [Device("TRK-001", 1)]).Write(path);

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        document.RootElement.GetProperty("ratePerSecond").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
