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
}
