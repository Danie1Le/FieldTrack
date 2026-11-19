using FieldTrack.Simulator;
using FieldTrack.Simulator.Bench;
using Npgsql;

namespace FieldTrack.UnitTests;

public sealed class BenchCliTests
{
    [Fact]
    public void The_password_comes_from_the_environment()
    {
        var connection = new NpgsqlConnectionStringBuilder(BenchCli.DatabaseConnection("Host=db.example;Database=fieldtrack;Username=bench", "s3cret"));

        connection.Host.ShouldBe("db.example");
        connection.Password.ShouldBe("s3cret");
    }

    [Fact]
    public void A_database_on_this_machine_falls_back_to_the_development_password()
    {
        new NpgsqlConnectionStringBuilder(BenchCli.DatabaseConnection(null, null)).Password.ShouldBe("fieldtrack");
    }

    [Fact]
    public void A_database_elsewhere_without_a_password_is_refused()
    {
        Should.Throw<SimulatorException>(() => BenchCli.DatabaseConnection("Host=db.example;Username=bench", null));
    }

    [Fact]
    public void A_password_on_the_command_line_is_refused()
    {
        Should.Throw<SimulatorException>(() => BenchCli.DatabaseConnection("Host=localhost;Username=bench;Password=oops", null));
    }

    [Fact]
    public void A_bulk_copy_may_stall_for_minutes_without_timing_out()
    {
        // Npgsql applies the command timeout to every write of a COPY stream, and a busy server stops reading it while it maintains indexes.
        new NpgsqlConnectionStringBuilder(BenchCli.DatabaseConnection(null, null)).CommandTimeout.ShouldBe(BenchDatabase.CommandTimeoutSeconds);
    }

    [Fact]
    public void A_command_timeout_in_the_database_option_is_kept()
    {
        new NpgsqlConnectionStringBuilder(BenchCli.DatabaseConnection("Host=localhost;Username=bench;Command Timeout=1800", null)).CommandTimeout.ShouldBe(1800);
    }

    [Fact]
    public void The_geofence_shape_defaults_to_the_five_position_square()
    {
        BenchCli.Vertices(null).ShouldBe(5);
        BenchCli.Vertices(65).ShouldBe(65);
    }

    [Theory]
    [InlineData(5.5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_fractional_vertex_count_is_refused(double vertices)
    {
        Should.Throw<SimulatorException>(() => BenchCli.Vertices(vertices));
    }

    [Fact]
    public void The_history_window_defaults_to_thirty_days_every_eight_minutes()
    {
        BenchCli.HistoryWindow(null, null).ShouldBe((TimeSpan.FromDays(30), TimeSpan.FromMinutes(8)));
    }

    [Theory]
    [InlineData(null, 0.0)]
    [InlineData(null, -480.0)]
    [InlineData(null, 0.5)]
    [InlineData(null, double.NaN)]
    [InlineData(null, double.PositiveInfinity)]
    [InlineData(0.0, null)]
    [InlineData(-1.0, null)]
    [InlineData(double.NaN, null)]
    [InlineData(double.PositiveInfinity, null)]
    public void An_unusable_history_window_is_refused(double? days, double? interval)
    {
        Should.Throw<SimulatorException>(() => BenchCli.HistoryWindow(days, interval));
    }

    [Fact]
    public void The_clock_offset_is_measured_unless_one_is_given()
    {
        BenchCli.ClockOffset(null).ShouldBeNull();
        BenchCli.ClockOffset(14).ShouldBe(TimeSpan.FromMilliseconds(14));
        BenchCli.ClockOffset(-3.5).ShouldBe(TimeSpan.FromMilliseconds(-3.5));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(3_600_001.0)]
    public void An_unusable_clock_offset_is_refused(double milliseconds)
    {
        Should.Throw<SimulatorException>(() => BenchCli.ClockOffset(milliseconds)).Message.ShouldContain("--clock-offset-ms");
    }

    [Theory]
    [InlineData("throughput", "--mqtt-transport", "wss")]
    [InlineData("offline", "--mqtt-transport", "ws")]
    [InlineData("hysteresis", "--clock-offset-ms", "14")]
    [InlineData("flood", "--mqtt-username", "simulator")]
    [InlineData("crash", "--mqtt-path", "/ws")]
    public async Task Only_alert_latency_runs_without_the_local_broker_and_database(string kind, string option, string value)
    {
        var refused = await Should.ThrowAsync<SimulatorException>(() => BenchCli.RunAsync(kind, Options.Parse([option, value]), CancellationToken.None));

        refused.Message.ShouldContain(option);
        refused.Message.ShouldContain("alert-latency");
    }

    [Fact]
    public async Task A_remote_API_needs_neither_a_compose_project_nor_a_database()
    {
        await using var target = BenchCli.TargetFor("alert-latency", Options.Parse(["--api", "https://demo.example/api", "--clock-offset-ms", "14"]));

        target.Remote.ShouldBeTrue();
    }

    [Fact]
    public void A_local_API_still_requires_the_compose_project()
    {
        Should.Throw<SimulatorException>(() => BenchCli.TargetFor("alert-latency", Options.Parse(["--api", "http://localhost:8080"]))).Message.ShouldContain("--compose-project");
    }

    [Fact]
    public void A_remote_run_without_a_clock_offset_is_refused_because_there_is_no_database_to_measure_it_on()
    {
        Should.Throw<SimulatorException>(() => BenchCli.TargetFor("alert-latency", Options.Parse(["--api", "https://demo.example/api"]))).Message.ShouldContain("--clock-offset-ms");
    }

    [Fact]
    public async Task A_remote_result_records_the_target_and_captures_no_local_stack()
    {
        var directory = Directory.CreateTempSubdirectory("ft-51-").FullName;
        try
        {
            await using var target = BenchCli.TargetFor("alert-latency", Options.Parse(["--api", "https://demo.example/api", "--clock-offset-ms", "14", "--out", directory]));

            var outcome = await target.WriteResultAsync("alert-latency", DateTimeOffset.UtcNow, new { }, new { }, pass: true, CancellationToken.None);

            var environment = System.Text.Json.JsonDocument.Parse(outcome.Json).RootElement.GetProperty("environment");
            environment.GetProperty("target").GetString().ShouldBe("remote");
            environment.GetProperty("containers").GetArrayLength().ShouldBe(0);
            environment.GetProperty("postgres").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
            environment.GetProperty("docker").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
