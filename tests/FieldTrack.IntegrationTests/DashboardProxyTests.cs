using System.Text;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;

namespace FieldTrack.IntegrationTests;

/// <summary>
/// The dashboard's nginx configuration (<c>web/nginx.conf.template</c>) on the runtime image and settings of
/// <c>web/Dockerfile</c>: it proxies <c>/api/</c> to whatever <c>FIELDTRACK_API_URL</c> names, so the same image
/// serves docker-compose (<c>http://api:8080</c>) and Container Apps (the API's internal address, #156).
/// The Angular build stage is skipped: this checks the proxy, not the app.
/// </summary>
public sealed partial class DashboardProxyTests : IAsyncLifetime
{
    private const string UpstreamAlias = "somewhere-else";

    private static readonly string[] _dockerfile = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "web", "Dockerfile"));

    private readonly INetwork _network = new NetworkBuilder().Build();
    private IContainer? _upstream;
    private IContainer? _dashboard;

    [Fact]
    public async Task Dashboard_proxies_api_requests_to_the_address_in_FIELDTRACK_API_URL()
    {
        using var http = new HttpClient
        {
            BaseAddress = new UriBuilder("http", _dashboard!.Hostname, _dashboard.GetMappedPublicPort(80)).Uri,
        };

        var answer = await http.GetStringAsync("/api/devices?page=2", TestContext.Current.CancellationToken);

        // The /api prefix is stripped, and the upstream sees its own host name: Container Apps routes on it.
        answer.ShouldBe($"{UpstreamAlias}:8080 /devices?page=2\n");
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        await _network.CreateAsync();
        var runtimeImage = _dockerfile.Select(line => RuntimeStage().Match(line)).Single(match => match.Success).Groups["image"].Value;

        // Stands in for the API: echoes the Host header and the path it was asked for.
        _upstream = new ContainerBuilder(runtimeImage)
            .WithNetwork(_network)
            .WithNetworkAliases(UpstreamAlias)
            .WithResourceMapping(
                Encoding.UTF8.GetBytes("server { listen 8080; location / { return 200 \"$http_host $request_uri\\n\"; } }\n"),
                "/etc/nginx/conf.d/default.conf")
            .Build();
        await _upstream.StartAsync();

        var dashboard = new ContainerBuilder(runtimeImage)
            .WithNetwork(_network)
            .WithPortBinding(80, assignRandomHostPort: true)
            // Where web/Dockerfile puts it, replacing the image's default server.
            .WithResourceMapping(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "web", "nginx.conf.template")), "/etc/nginx/templates/default.conf.template")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("start worker process"));
        foreach (var match in _dockerfile.Select(line => EnvLine().Match(line)).Where(match => match.Success))
        {
            dashboard = dashboard.WithEnvironment(match.Groups["name"].Value, match.Groups["value"].Value);
        }

        _dashboard = dashboard.WithEnvironment("FIELDTRACK_API_URL", $"http://{UpstreamAlias}:8080").Build();
        await _dashboard.StartAsync();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var container in new[] { _dashboard, _upstream })
        {
            if (container is not null)
            {
                await container.DisposeAsync();
            }
        }

        await _network.DisposeAsync();
    }

    [GeneratedRegex(@"^FROM\s+(?<image>\S+)\s+AS\s+runtime\s*$")]
    private static partial Regex RuntimeStage();

    [GeneratedRegex(@"^ENV\s+(?<name>[A-Z_]+)=(?<value>\S+)\s*$")]
    private static partial Regex EnvLine();
}
