using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace FieldTrack.IntegrationTests;

public sealed class ObservabilityTests(FieldTrackFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Exported_traces_never_carry_the_SignalR_access_token_from_the_query_string()
    {
        const string secret = "secret-access-token-0123456789";
        var exported = new CollectingProcessor();
        await using var api = Fixture.Api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddOpenTelemetry().WithTracing(tracing => tracing.AddProcessor(exported))));
        using var client = api.CreateClient();

        // Refused for want of a valid token, but the request still has its span.
        using var response = await client.PostAsync($"/hubs/live/negotiate?negotiateVersion=1&access_token={secret}", content: null, CancellationToken);
        api.Services.GetRequiredService<TracerProvider>().ForceFlush();

        // Every host in the test process shares the ActivitySources, so this also sees other hosts' requests.
        var spans = exported.Ended.ToArray();
        spans.ShouldContain(activity => activity.TagObjects.Contains(new KeyValuePair<string, object?>("url.path", "/hubs/live/negotiate")));
        foreach (var activity in spans)
        {
            activity.DisplayName.ShouldNotContain(secret);
            activity.TagObjects.Select(tag => tag.Value?.ToString()).ShouldAllBe(value => value == null || !value.Contains(secret));
        }
    }

    [Fact]
    public async Task The_host_starts_with_Azure_Monitor_configured_and_serves_health()
    {
        await using var api = Fixture.Api.WithWebHostBuilder(builder => builder
            // Nothing listens on port 1: telemetry is dropped and the host must not care.
            .UseSetting("APPLICATIONINSIGHTS_CONNECTION_STRING", "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=http://127.0.0.1:1/")
            .UseSetting("AzureMonitor:DisableOfflineStorage", "true"));
        using var client = api.CreateClient();

        using var response = await client.GetAsync("/health", CancellationToken);

        response.EnsureSuccessStatusCode();
    }

    /// <summary>Keeps every span that ends, as an exporter would receive it.</summary>
    private sealed class CollectingProcessor : BaseProcessor<Activity>
    {
        public ConcurrentQueue<Activity> Ended { get; } = new();

        public override void OnEnd(Activity data) => Ended.Enqueue(data);
    }
}
