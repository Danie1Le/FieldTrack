using System.Text.RegularExpressions;

namespace FieldTrack.UnitTests;

public sealed partial class ComposePortsTests
{
    [Fact]
    public void Every_published_port_binds_to_loopback()
    {
        // The API runs in Development (developer exception page, plain HTTP) and the broker and
        // database use well-known credentials, so none may be reachable from the network (#61).
        var ports = PortMapping()
            .Matches(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "docker-compose.yml")))
            .Select(match => match.Groups["port"].Value)
            .ToList();

        ports.ShouldNotBeEmpty();
        ports.ShouldAllBe(port => port.StartsWith("127.0.0.1:", StringComparison.Ordinal));
    }

    [GeneratedRegex(@"^\s*-\s*""(?<port>\d[^""]*:\d+)""\s*$", RegexOptions.Multiline)]
    private static partial Regex PortMapping();
}
