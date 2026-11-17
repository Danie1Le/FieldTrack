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
}
