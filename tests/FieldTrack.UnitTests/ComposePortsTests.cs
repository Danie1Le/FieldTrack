using YamlDotNet.RepresentationModel;

namespace FieldTrack.UnitTests;

public sealed class ComposePortsTests
{
    [Fact]
    public void Every_published_port_binds_to_loopback()
    {
        // The API runs in Development (developer exception page, plain HTTP) and the broker and
        // database use well-known credentials, so none may be reachable from the network (#61).
        using var compose = File.OpenText(Path.Combine(AppContext.BaseDirectory, "docker-compose.yml"));

        var ports = PublishedPorts(compose);

        ports.ShouldNotBeEmpty();
        ports.ShouldAllBe(port => IsLoopback(port));
    }

    [Theory]
    [InlineData("- \"127.0.0.1:8080:8080\" # note", true)]
    [InlineData("- target: 8080\n        host_ip: 127.0.0.1", true)]
    [InlineData("- 8080:8080", false)]
    [InlineData("- '8080:8080'", false)]
    [InlineData("- \"8080:8080\" # note", false)]
    [InlineData("- \"8080:8080/tcp\"", false)]
    [InlineData("- \"8080\"", false)]
    [InlineData("- \"0.0.0.0:8080:8080\"", false)]
    [InlineData("- target: 8080\n        published: 8080", false)]
    [InlineData("- target: 8080\n        host_ip: 0.0.0.0", false)]
    public void Port_entries_are_judged_in_every_compose_syntax(string entry, bool loopback)
    {
        using var compose = new StringReader($"services:\n  api:\n    ports:\n      {entry}\n");

        IsLoopback(PublishedPorts(compose).ShouldHaveSingleItem()).ShouldBe(loopback);
    }

    [Fact]
    public void Ports_in_a_flow_sequence_are_found()
    {
        using var compose = new StringReader("services:\n  api:\n    ports: [\"8080:8080\"]\n");

        PublishedPorts(compose).ShouldHaveSingleItem();
    }

    private static List<YamlNode> PublishedPorts(TextReader compose)
    {
        var yaml = new YamlStream();
        yaml.Load(compose);
        var services = (YamlMappingNode)((YamlMappingNode)yaml.Documents[0].RootNode)["services"];
        return services.Children.Values
            .Cast<YamlMappingNode>()
            .Where(service => service.Children.ContainsKey("ports"))
            .SelectMany(service => (YamlSequenceNode)service["ports"])
            .ToList();
    }

    // Short syntax "127.0.0.1:8080:8080", or long syntax with host_ip; anything else binds every interface.
    private static bool IsLoopback(YamlNode port) => port switch
    {
        YamlScalarNode shortSyntax => shortSyntax.Value!.StartsWith("127.0.0.1:", StringComparison.Ordinal),
        YamlMappingNode longSyntax => longSyntax.Children.TryGetValue("host_ip", out var hostIp)
            && hostIp is YamlScalarNode { Value: "127.0.0.1" },
        _ => false,
    };
}
