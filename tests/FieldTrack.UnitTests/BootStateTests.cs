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
    public void A_Device_not_in_the_file_starts_at_the_first_Boot_given_and_one_in_it_at_its_next()
    {
        BootState.Raise(StatePath, ["TRK-001"]);

        var boots = BootState.Raise(StatePath, ["TRK-001", "GEN-001"], firstBoot: 500);

        boots.ShouldBe(new Dictionary<string, int> { ["TRK-001"] = 2, ["GEN-001"] = 500 });
    }

    [Fact]
    public void A_clock_Boot_counts_the_seconds_since_2026_so_a_later_start_gets_a_higher_one()
    {
        var start = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);

        BootState.ClockBoot(start).ShouldBe(86_400);
        BootState.ClockBoot(start.AddSeconds(1)).ShouldBe(86_401);
    }

    [Fact]
    public async Task Devices_of_one_run_that_power_cycle_together_each_get_their_own_Boot()
    {
        BootState.Raise(StatePath, ["TRK-001"]);

        var boots = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => BootState.Raise(StatePath, ["TRK-001"])["TRK-001"], TestContext.Current.CancellationToken)));

        boots.Order().ShouldBe(Enumerable.Range(2, 16));
    }

    [Fact]
    public void Raising_leaves_no_temporary_file_behind()
    {
        BootState.Raise(StatePath, ["TRK-001"]);

        File.Exists(StatePath + ".tmp").ShouldBeFalse();
    }

    [Fact]
    public void A_second_simulator_cannot_raise_the_Boots_while_the_first_one_is()
    {
        using (new FileStream(StatePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Should.Throw<SimulatorException>(() => BootState.Raise(StatePath, ["TRK-001"])).Message.ShouldContain("Another simulator");
        }

        BootState.Raise(StatePath, ["TRK-001"]).ShouldBe(new Dictionary<string, int> { ["TRK-001"] = 1 });
    }

    [Fact]
    public void Simulators_raising_at_the_same_moment_never_get_the_same_Boot()
    {
        var boots = new System.Collections.Concurrent.ConcurrentBag<int>();

        Parallel.For(0, 8, _ =>
        {
            try
            {
                boots.Add(BootState.Raise(StatePath, ["TRK-001"])["TRK-001"]);
            }
            catch (SimulatorException)
            {
                // Refused because another one held the lock: that simulator does not start.
            }
        });

        boots.ShouldNotBeEmpty();
        boots.ShouldBeUnique();
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
