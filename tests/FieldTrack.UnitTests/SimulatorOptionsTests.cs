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

    private static readonly FleetFile Fleet = FleetFile.Parse(
        """{ "devices": [ { "serial": "TRK-001", "assetType": "Truck", "sensors": ["fuelPct"] } ] }""", AppContext.BaseDirectory);

    [Fact]
    public void A_run_takes_a_rate_or_max_and_the_random_injection_rates()
    {
        var paced = SimulatorCli.RunOptionsFrom(
            Options.Parse(["--rate", "25", "--fault-rate", "2", "--power-off-rate", "0.5", "--coverage-gap-rate", "1", "--duration", "60"]), Fleet);
        var flood = SimulatorCli.RunOptionsFrom(Options.Parse(["--rate", "max"]), Fleet);

        paced.RatePerSecond.ShouldBe(25);
        paced.RandomInjection.ShouldBe(new RandomRates(2, 0.5, 1));
        paced.Duration.ShouldBe(TimeSpan.FromMinutes(1));
        paced.Scenario.ShouldBeNull();
        flood.RatePerSecond.ShouldBeNull();
        flood.RandomInjection.ShouldBe(RandomRates.None);
    }

    [Fact]
    public void A_run_starts_new_Devices_at_Boot_1_or_with_first_boot_clock_at_the_clock_Boot()
    {
        var before = BootState.ClockBoot(DateTimeOffset.UtcNow);
        var clock = SimulatorCli.RunOptionsFrom(Options.Parse(["--rate", "1", "--first-boot", "clock"]), Fleet);
        var after = BootState.ClockBoot(DateTimeOffset.UtcNow);

        SimulatorCli.RunOptionsFrom(Options.Parse(["--rate", "1"]), Fleet).FirstBoot.ShouldBe(1);
        clock.FirstBoot.ShouldBeInRange(before, after);
        Should.Throw<SimulatorException>(() => SimulatorCli.RunOptionsFrom(Options.Parse(["--rate", "1", "--first-boot", "7"]), Fleet))
            .Message.ShouldContain("--first-boot");
    }

    [Fact]
    public void A_Scenario_file_is_loaded_against_the_fleet()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """{ "injections": [ { "device": "TRK-001", "kind": "powerOff", "atSeconds": 5, "durationSeconds": 10 } ] }""");

            var run = SimulatorCli.RunOptionsFrom(Options.Parse(["--rate", "10", "--scenario", path]), Fleet);

            run.Scenario!.InjectionsFor("TRK-001").ShouldHaveSingleItem().Kind.ShouldBe(InjectionKind.PowerOff);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("--rate", "fast", "--rate must be a number")]
    [InlineData("--fault-rate", "-1", "--fault-rate must be")]
    [InlineData("--power-off-rate", "NaN", "--power-off-rate must be")]
    [InlineData("--coverage-gap-rate", "Infinity", "--coverage-gap-rate must be")]
    [InlineData("--mqtt-port", "70000", "--mqtt-port")]
    [InlineData("--duration", "-5", "--duration")]
    public void A_run_option_out_of_range_is_refused(string name, string value, string reason)
    {
        string[] args = name == "--rate" ? [name, value] : ["--rate", "10", name, value];

        Should.Throw<SimulatorException>(() => SimulatorCli.RunOptionsFrom(Options.Parse(args), Fleet)).Message.ShouldContain(reason);
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
    [InlineData("https://dashboard.example/api")]
    [InlineData("https://dashboard.example/api/")]
    public async Task An_API_behind_a_path_prefix_gets_every_call_under_that_prefix(string address)
    {
        var requested = new List<string>();
        using var http = new HttpClient(new StubHandler(request =>
        {
            requested.Add(request.RequestUri!.AbsoluteUri);
            return request.Method == HttpMethod.Post ? """{ "accessToken": "token" }""" : "[]";
        }))
        { BaseAddress = SimulatorCli.ApiAddress(address) };

        var api = await FieldTrackApi.SignInAsync(http, "admin@example.com", "password", CancellationToken.None);
        await api.ListAssetsAsync(CancellationToken.None);

        requested.ShouldBe(["https://dashboard.example/api/auth/sign-in", "https://dashboard.example/api/assets"]);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(respond(request), System.Text.Encoding.UTF8, "application/json"),
            });
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

    [Theory]
    [InlineData(new string[0], "mqtt://localhost:1883/")]
    [InlineData(new[] { "--mqtt-host", "broker.example", "--mqtt-port", "1884" }, "mqtt://broker.example:1884/")]
    [InlineData(new[] { "--mqtt-transport", "ws", "--mqtt-host", "127.0.0.1", "--mqtt-port", "8080" }, "ws://127.0.0.1:8080/mqtt")]
    [InlineData(new[] { "--mqtt-transport", "wss", "--mqtt-host", "broker.example" }, "wss://broker.example:443/mqtt")]
    [InlineData(new[] { "--mqtt-transport", "wss", "--mqtt-host", "broker.example", "--mqtt-path", "/ws" }, "wss://broker.example:443/ws")]
    public void A_run_reaches_the_broker_over_TCP_or_WebSockets(string[] args, string address)
    {
        var broker = SimulatorCli.RunOptionsFrom(Options.Parse([.. args, "--rate", "1"]), Fleet).Broker;

        broker.Address.ShouldBe(new Uri(address));
        broker.Username.ShouldBeNull();
    }

    [Theory]
    [InlineData("tcp", "broker.example", "only over wss")]
    [InlineData("ws", "broker.example", "only over wss")]
    [InlineData("quic", "localhost", "--mqtt-transport must be tcp, ws or wss")]
    public void A_broker_password_goes_unencrypted_only_to_this_machine(string transport, string host, string reason)
    {
        var options = Options.Parse(["--rate", "1", "--mqtt-transport", transport, "--mqtt-host", host, "--mqtt-username", "simulator"]);

        Should.Throw<SimulatorException>(() => SimulatorCli.RunOptionsFrom(options, Fleet)).Message.ShouldContain(reason);
    }

    [Theory]
    [InlineData("wss", "broker.example", "wss://broker.example:443/mqtt")]
    [InlineData("tcp", "localhost", "mqtt://localhost:1883/")]
    [InlineData("ws", "127.0.0.1", "ws://127.0.0.1:80/mqtt")]
    public void A_broker_user_connects_with_the_password_from_the_environment(string transport, string host, string address)
    {
        var options = Options.Parse(["--rate", "1", "--mqtt-transport", transport, "--mqtt-host", host, "--mqtt-username", "simulator"]);

        var broker = SimulatorCli.RunOptionsFrom(options, Fleet, name => name == "FIELDTRACK_MQTT_PASSWORD" ? "s3cret" : null).Broker;

        broker.ShouldBe(new MqttBroker(new Uri(address), "simulator", "s3cret"));
    }

    [Fact]
    public void A_broker_user_without_a_password_is_refused()
    {
        var options = Options.Parse(["--rate", "1", "--mqtt-transport", "wss", "--mqtt-host", "broker.example", "--mqtt-username", "simulator"]);

        Should.Throw<SimulatorException>(() => SimulatorCli.RunOptionsFrom(options, Fleet, _ => null)).Message.ShouldContain("Set FIELDTRACK_MQTT_PASSWORD");
    }

    [Fact]
    public void A_broker_never_prints_its_password() =>
        new MqttBroker(new Uri("wss://broker.example/mqtt"), "simulator", "s3cret").ToString().ShouldNotContain("s3cret");
}
