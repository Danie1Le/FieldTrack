using FieldTrack.Simulator;

namespace FieldTrack.UnitTests;

public sealed class BootStateTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("fieldtrack-boot-").FullName;

    private string StatePath => Path.Combine(_directory, "state.json");

    [Fact]
    public void The_first_start_gives_every_Device_Boot_1_and_each_later_start_the_next()
    {
        var first = BootState.Raise(StatePath, ["TRK-001", "GEN-001"]);
        var second = BootState.Raise(StatePath, ["TRK-001", "GEN-001"]);

        first.ShouldBe(new Dictionary<string, int> { ["TRK-001"] = 1, ["GEN-001"] = 1 });
        second.ShouldBe(new Dictionary<string, int> { ["TRK-001"] = 2, ["GEN-001"] = 2 });
    }

    [Fact]
    public void A_Device_added_later_starts_at_1_and_one_left_out_keeps_its_count()
    {
        BootState.Raise(StatePath, ["TRK-001"]);
        BootState.Raise(StatePath, ["TRK-001"]);

        var onlyNew = BootState.Raise(StatePath, ["GEN-001"]);
        var both = BootState.Raise(StatePath, ["TRK-001", "GEN-001"]);

        onlyNew.ShouldBe(new Dictionary<string, int> { ["GEN-001"] = 1 });
        both.ShouldBe(new Dictionary<string, int> { ["TRK-001"] = 3, ["GEN-001"] = 2 });
    }

    [Fact]
    public void The_counts_are_saved_before_Raise_returns()
    {
        BootState.Raise(StatePath, ["TRK-001"]);

        File.ReadAllText(StatePath).ShouldContain("\"TRK-001\": 1");
    }

    [Fact]
    public void A_state_file_that_cannot_be_read_is_an_error_and_is_left_alone()
    {
        // Starting over from 0 would reuse (Device, Boot, Sequence number) identities (ADR 0001).
        File.WriteAllText(StatePath, "{ not json");

        var exception = Should.Throw<SimulatorException>(() => BootState.Raise(StatePath, ["TRK-001"]));

        exception.Message.ShouldContain(StatePath);
        File.ReadAllText(StatePath).ShouldBe("{ not json");
    }

    [Fact]
    public void The_state_file_is_created_in_a_directory_that_does_not_exist_yet()
    {
        var path = Path.Combine(_directory, "nested", "state.json");

        BootState.Raise(path, ["TRK-001"]);

        File.Exists(path).ShouldBeTrue();
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
