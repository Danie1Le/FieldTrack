using FieldTrack.Simulator;

namespace FieldTrack.UnitTests;

public sealed class SimulatorOptionsTests
{
    [Fact]
    public void Name_value_pairs_are_read()
    {
        var options = Options.Parse(["--fleet", "demo.json", "--api", "http://localhost:8080"]);

        options.Get("fleet").ShouldBe("demo.json");
        options.Get("api").ShouldBe("http://localhost:8080");
        options.Get("email").ShouldBeNull();
    }

    [Theory]
    [InlineData(new[] { "--fleet" }, "--fleet")]
    [InlineData(new[] { "fleet", "demo.json" }, "fleet")]
    [InlineData(new[] { "--fleet", "--api", "x" }, "--fleet")]
    [InlineData(new[] { "--fleet", "a.json", "--fleet", "b.json" }, "given twice")]
    public void Arguments_that_are_not_name_value_pairs_are_refused(string[] args, string reason)
    {
        Should.Throw<SimulatorException>(() => Options.Parse(args)).Message.ShouldContain(reason);
    }

    [Theory]
    [InlineData("http://localhost:8080")]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("http://[::1]:8080")]
    [InlineData("https://fieldtrack.example.com")]
    public void The_API_address_is_https_or_plain_http_on_this_machine(string address)
    {
        SimulatorCli.ApiAddress(address).ShouldBe(new Uri(address));
    }

    [Theory]
    [InlineData("http://fieldtrack.example.com", "cleartext")]
    [InlineData("localhost:8080", "absolute http or https")]
    [InlineData("ftp://localhost", "absolute http or https")]
    [InlineData("not a url", "absolute http or https")]
    public void Any_other_API_address_is_refused(string address, string reason)
    {
        Should.Throw<SimulatorException>(() => SimulatorCli.ApiAddress(address)).Message.ShouldContain(reason);
    }
}
