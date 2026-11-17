using FieldTrack.Simulator.Bench;

namespace FieldTrack.UnitTests;

public sealed class DockerIngestTests
{
    [Theory]
    [InlineData("127.0.0.1:18044", 18044, true)]
    [InlineData("0.0.0.0:8080\n[::]:8080", 8080, true)]
    [InlineData("127.0.0.1:8080", 18044, false)]
    [InlineData("", 8080, false)]
    public void A_container_serves_the_api_only_when_it_publishes_the_port_the_api_address_names(string dockerPort, int apiPort, bool serves)
    {
        DockerIngest.ServesPort(dockerPort, apiPort).ShouldBe(serves);
    }
}
