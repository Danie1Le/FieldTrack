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
    public void The_file_is_JSON_a_benchmark_can_read_back()
    {
        var device = Device("TRK-001", boot: 2);
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
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
