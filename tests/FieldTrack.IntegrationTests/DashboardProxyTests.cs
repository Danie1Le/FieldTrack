using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;

namespace FieldTrack.IntegrationTests;

/// <summary>
/// The dashboard's nginx configuration (<c>web/nginx.conf.template</c>) on the runtime image and settings of
/// <c>web/Dockerfile</c>: it proxies <c>/api/</c> to whatever <c>FIELDTRACK_API_URL</c> names, so the same image
/// serves docker-compose (<c>http://api:8080</c>) and Container Apps (the API's internal HTTPS address, #156).
/// The Angular build stage is skipped: this checks the proxy, not the app.
/// </summary>
public sealed partial class DashboardProxyTests : IAsyncLifetime
{
    private const string UpstreamAlias = "somewhere-else";

    private static readonly string[] _dockerfile = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "web", "Dockerfile"));

    private readonly INetwork _network = new NetworkBuilder().Build();
    private readonly List<IContainer> _containers = [];
    private string _runtimeImage = "";

    [Fact]
    public async Task Dashboard_proxies_api_requests_to_the_address_in_FIELDTRACK_API_URL()
    {
        using var http = await StartDashboardAsync($"http://{UpstreamAlias}:8080");

        var answer = await http.GetStringAsync("/api/devices?page=2", TestContext.Current.CancellationToken);

        // The /api prefix is stripped, and the upstream sees its own host name: Container Apps routes on it.
        answer.ShouldBe($"http {UpstreamAlias}:8080 /devices?page=2 sni= upgrade=\n");
    }

    [Fact]
    public async Task Dashboard_ignores_a_trailing_slash_in_FIELDTRACK_API_URL()
    {
        using var http = await StartDashboardAsync($"http://{UpstreamAlias}:8080/");

        var answer = await http.GetStringAsync("/api/devices?page=2", TestContext.Current.CancellationToken);

        answer.ShouldBe($"http {UpstreamAlias}:8080 /devices?page=2 sni= upgrade=\n");
    }

    [Fact]
    public async Task Dashboard_sends_the_api_host_name_in_the_TLS_handshake_to_an_https_upstream()
    {
        using var http = await StartDashboardAsync($"https://{UpstreamAlias}:8443");

        var answer = await http.GetStringAsync("/api/devices", TestContext.Current.CancellationToken);

        // Container Apps picks the app by SNI as well as Host on its internal HTTPS address.
        answer.ShouldBe($"https {UpstreamAlias}:8443 /devices sni={UpstreamAlias} upgrade=\n");
    }

    [Fact]
    public async Task Dashboard_passes_the_websocket_upgrade_through_to_the_hub()
    {
        using var http = await StartDashboardAsync($"http://{UpstreamAlias}:8080");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/hubs/readings?id=1");
        request.Headers.Upgrade.Add(new ProductHeaderValue("websocket"));
        request.Headers.Connection.Add("Upgrade");

        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);

        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldBe($"http {UpstreamAlias}:8080 /hubs/readings?id=1 sni= upgrade=websocket\n");
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        await _network.CreateAsync();
        _runtimeImage = _dockerfile.Select(line => RuntimeStage().Match(line)).Single(match => match.Success).Groups["image"].Value;

        // A throwaway self-signed certificate: nginx does not verify upstream certificates by default (proxy_ssl_verify off).
        using var key = RSA.Create(2048);
        var certificateRequest = new CertificateRequest($"CN={UpstreamAlias}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        // Stands in for the API over HTTP and HTTPS: echoes the scheme, Host header, path, TLS server name and Upgrade header.
        var upstream = new ContainerBuilder(_runtimeImage)
            .WithNetwork(_network)
            .WithNetworkAliases(UpstreamAlias)
            .WithResourceMapping(Encoding.UTF8.GetBytes(certificate.ExportCertificatePem()), "/etc/nginx/upstream.crt")
            .WithResourceMapping(Encoding.UTF8.GetBytes(key.ExportPkcs8PrivateKeyPem()), "/etc/nginx/upstream.key")
            .WithResourceMapping(
                Encoding.UTF8.GetBytes(
                    "server { listen 8080; listen 8443 ssl; ssl_certificate /etc/nginx/upstream.crt; ssl_certificate_key /etc/nginx/upstream.key; "
                    + "location / { return 200 \"$scheme $http_host $request_uri sni=$ssl_server_name upgrade=$http_upgrade\\n\"; } }\n"),
                "/etc/nginx/conf.d/default.conf")
            .Build();
        _containers.Add(upstream);
        await upstream.StartAsync();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var container in _containers)
        {
            await container.DisposeAsync();
        }

        await _network.DisposeAsync();
    }

    private async Task<HttpClient> StartDashboardAsync(string apiUrl)
    {
        var builder = new ContainerBuilder(_runtimeImage)
            .WithNetwork(_network)
            .WithPortBinding(80, assignRandomHostPort: true)
            // Where web/Dockerfile puts it, replacing the image's default server.
            .WithResourceMapping(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "web", "nginx.conf.template")), "/etc/nginx/templates/default.conf.template")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("start worker process"));
        foreach (var match in _dockerfile.Select(line => EnvLine().Match(line)).Where(match => match.Success))
        {
            builder = builder.WithEnvironment(match.Groups["name"].Value, match.Groups["value"].Value);
        }

        var dashboard = builder.WithEnvironment("FIELDTRACK_API_URL", apiUrl).Build();
        _containers.Add(dashboard);
        await dashboard.StartAsync(TestContext.Current.CancellationToken);

        return new HttpClient { BaseAddress = new UriBuilder("http", dashboard.Hostname, dashboard.GetMappedPublicPort(80)).Uri };
    }

    [GeneratedRegex(@"^FROM\s+(?<image>\S+)\s+AS\s+runtime\s*$")]
    private static partial Regex RuntimeStage();

    [GeneratedRegex(@"^ENV\s+(?<name>[A-Z_]+)=(?<value>\S+)\s*$")]
    private static partial Regex EnvLine();
}
